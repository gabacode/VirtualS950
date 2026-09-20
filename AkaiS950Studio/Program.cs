using System;
using System.Windows.Forms;

namespace AkaiS950Studio
{
    internal static class EntryPoint
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }
}
