// Builds manual.<lang>.html from the READMEs, then prints each to PDF with Edge (Noto Sans KR/JP must be installed).
// Run: node docs/manual/make-pdf.mjs
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
const root = join(here, '..', '..')
const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
const version = readFileSync(join(root, 'src/MonitorBridge/MonitorBridge.csproj'), 'utf8').match(/<Version>(.+?)<\/Version>/)[1]
const date = new Date().toLocaleDateString('sv-SE') // local YYYY-MM-DD

const langs = {
	ko: {
		readme: 'README.md', name: 'Monitor Bridge 사용설명서', author: '만든 사람', skip: ['다운로드', '직접 빌드하기'],
		contact: ['문의하기', '사용 중 문제가 있거나 궁금한 점이 있으면 만든 사람(김탄2)에게 문의해 주세요.', '치지직(Chzzk): 김탄2'],
	},
	en: {
		readme: 'README.en.md', name: 'Monitor Bridge Manual (English)', author: 'Made by', skip: ['Download', 'Building'],
		contact: ['Contact', 'If you have a problem or a question, contact the author (Kimtan2).', 'Chzzk: 김탄2'],
	},
	ja: {
		readme: 'README.ja.md', name: 'Monitor Bridge 取扱説明書 (日本語)', author: '作成者', skip: ['ダウンロード', 'ビルド'],
		contact: ['お問い合わせ', '使用中に問題や質問があれば、作成者（김탄2）にお問い合わせください。', 'チジジク（Chzzk）: 김탄2'],
	},
}

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
// Only what the READMEs use: `code` / ``code with ` ``, **bold**, [text](url).
function inline(s) {
	const codes = []
	s = s.replace(/``\s?(.+?)\s?``|`([^`]+)`/g, (_, a, b) => `\u0000${codes.push(a ?? b) - 1}\u0000`)
	s = esc(s).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/\[(.+?)\]\((.+?)\)/g, '<a href="$2">$1</a>')
	return s.replace(/\u0000(\d+)\u0000/g, (_, i) => `<code>${esc(codes[i])}</code>`)
}
const cells = (line) => line.trim().replace(/^\||\|$/g, '').split(/(?<!`)\|(?!`)/).map((c) => inline(c.trim()))

function toHtml(lang, cfg) {
	const lines = readFileSync(join(root, cfg.readme), 'utf8').split(/\r?\n/)
	const out = []
	let inSection = false, skipping = false, list = null, fence = false
	const closeList = () => { if (list) { out.push(list === 'ol' ? '</ol>' : '</ul>'); list = null } }
	for (let i = 0; i < lines.length; i++) {
		const line = lines[i]
		if (line.startsWith('```')) { fence = !fence; continue }
		if (fence) continue
		if (line.startsWith('## ')) {
			closeList()
			if (inSection) out.push('</section>')
			skipping = cfg.skip.includes(line.slice(3).trim())
			inSection = !skipping
			if (!skipping) out.push(`<section><h2>${inline(line.slice(3))}</h2>`)
			continue
		}
		if (skipping || !inSection) continue
		if (/^<\/?(details|summary)/.test(line)) continue // folded in the README, printed open in the PDF
		if (line.startsWith('### ')) { closeList(); out.push(`<h3>${inline(line.slice(4))}</h3>`); continue }
		if (line.startsWith('|')) {
			closeList()
			const head = cells(line)
			i++ // separator row
			const rows = []
			while (i + 1 < lines.length && lines[i + 1].startsWith('|')) rows.push(cells(lines[++i]))
			out.push(`<table><thead><tr>${head.map((c) => `<th>${c}</th>`).join('')}</tr></thead><tbody>`)
			for (const r of rows) out.push(`<tr>${r.map((c) => `<td>${c}</td>`).join('')}</tr>`)
			out.push('</tbody></table>')
			continue
		}
		const ol = line.match(/^\d+\. (.*)/), ul = line.match(/^- (.*)/), sub = line.match(/^\s+- (.*)/)
		if (sub && list) { out[out.length - 1] = out[out.length - 1].replace(/<\/p><\/li>$/, `<br>${inline(sub[1])}</p></li>`); continue }
		if (ol || ul) {
			const kind = ol ? 'ol' : 'ul'
			if (list !== kind) { closeList(); out.push(`<${kind}>`); list = kind }
			out.push(`<li><p>${inline((ol ?? ul)[1])}</p></li>`)
			continue
		}
		closeList()
		if (line.startsWith('> ')) out.push(`<p>${inline(line.slice(2))}</p>`)
		else if (line.trim()) out.push(`<p>${inline(line)}</p>`)
	}
	closeList()
	if (inSection) out.push('</section>')

	const intro = lines.slice(1).filter((l) => l.trim() && !l.startsWith('**') && !l.startsWith('['))
	const firstH2 = intro.findIndex((l) => l.startsWith('#'))
	const introHtml = []
	let introList = false
	for (const l of intro.slice(0, firstH2)) {
		if (l.startsWith('- ')) { if (!introList) { introHtml.push('<ul>'); introList = true } introHtml.push(`<li><p>${inline(l.slice(2))}</p></li>`) }
		else { if (introList) { introHtml.push('</ul>'); introList = false } introHtml.push(`<p>${inline(l)}</p>`) }
	}
	if (introList) introHtml.push('</ul>')

	const [ctitle, ctext, cchzzk] = cfg.contact
	return `<!doctype html>
<html lang="${lang}">
<head>
<meta charset="utf-8">
<title>${cfg.name}</title>
<link rel="stylesheet" href="manual.css">
</head>
<body>
<h1>${cfg.name}</h1>
<p><time datetime="${date}">${date} (v${version})</time></p>
${introHtml.join('\n')}
<p>${cfg.author}: <strong>김탄2</strong></p>
${out.join('\n')}
<section><h2>${ctitle}</h2>
<p>${ctext}</p>
<ul>
<li><p>Discord: kimtan2</p></li>
<li><p>${cchzzk}</p></li>
</ul>
</section>
</body>
</html>
`
}

const sleep = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms)
// msedge.exe can return before the PDF is written: wait until the file exists and stops growing.
function waitForFile(file) {
	let last = -1
	for (let i = 0; i < 120; i++) {
		const size = existsSync(file) ? statSync(file).size : 0
		if (size > 0 && size === last) return size
		last = size
		sleep(500)
	}
	throw new Error(`no PDF written: ${file}`)
}

for (const [lang, cfg] of Object.entries(langs)) {
	const html = join(here, `manual.${lang}.html`)
	writeFileSync(html, toHtml(lang, cfg))
	const out = join(here, `${cfg.name}.pdf`)
	rmSync(out, { force: true })
	execFileSync(edge, ['--headless=new', '--disable-gpu', '--no-pdf-header-footer', `--print-to-pdf=${out}`, pathToFileURL(html).href], { stdio: 'ignore' })
	console.log(waitForFile(out), cfg.name)
}
