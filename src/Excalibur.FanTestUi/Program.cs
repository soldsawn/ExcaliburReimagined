namespace Excalibur.FanTestUi;

internal static class Program
{
    /// <summary>
    /// Small test window for the fan service. Talks to it only through the control pipe, so it
    /// needs no administrator rights and never touches the EC.
    ///   --sim               start connected to the --simulate instance
    ///   --snapshot <png>    (development) save the window to a PNG after a few seconds, then exit
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var form = new MainForm(startSimulated: args.Contains("--sim", StringComparer.OrdinalIgnoreCase));

        int snap = Array.FindIndex(args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
        if (snap >= 0 && snap + 1 < args.Length)
        {
            string path = args[snap + 1];
            var timer = new System.Windows.Forms.Timer { Interval = 6000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                using var bmp = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            };
            form.Shown += (_, _) => timer.Start();
        }

        Application.Run(form);
    }
}
