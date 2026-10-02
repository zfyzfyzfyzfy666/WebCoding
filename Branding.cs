using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace WebCodingBrand
{
    internal static class Branding
    {
        internal static void Apply(Form form)
        {
            form.Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        }

        internal static PictureBox Logo()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebCoding.Logo"))
            using (var image = Image.FromStream(stream))
                return new PictureBox { Image = new Bitmap(image), SizeMode = PictureBoxSizeMode.Zoom, TabStop = false };
        }
    }
}
