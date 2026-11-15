using System.Drawing;
using MonitorBridge.UI;

namespace MonitorBridge.Core.Tests;

public class IconsTests
{
    [Fact]
    public void WriteIco_HoldsEverySize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mb-icon-{Guid.NewGuid()}.ico");
        try
        {
            Icons.WriteIco(path, ColorTranslator.FromHtml("#1F8A84"));
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);
            Assert.Equal(Icons.IcoSizes.Length, BitConverter.ToUInt16(bytes, 4));
            foreach (int size in Icons.IcoSizes.Where(s => s < 256)) // System.Drawing skips PNG entries
            {
                using var icon = new Icon(path, size, size);
                Assert.Equal(size, icon.Width);
            }
            int last = 6 + 16 * (Icons.IcoSizes.Length - 1);
            Assert.Equal(0, bytes[last]); // 0 = 256 wide
            int offset = BitConverter.ToInt32(bytes, last + 12);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes[offset..(offset + 4)]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Render_DrawsInTheGivenColor()
    {
        var teal = ColorTranslator.FromHtml("#1F8A84");
        using var bmp = Icons.Render(32, teal);
        Assert.Equal(teal.ToArgb(), bmp.GetPixel(15, 16).ToArgb()); // inside the cursor
        Assert.Equal(0, bmp.GetPixel(0, 0).A); // transparent corner
    }
}
