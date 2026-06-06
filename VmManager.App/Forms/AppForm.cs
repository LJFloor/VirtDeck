namespace VmManager.Forms
{
    /// <summary>
    /// Base for every app window. Stamps the shared application icon so all title bars,
    /// taskbar buttons and Alt-Tab entries show the FatCow "computer" icon. Forms still
    /// build their own layout in their hand-written <c>*.Designer.cs</c>.
    /// </summary>
    public class AppForm : Form
    {
        public AppForm()
        {
            Icon = AppIcons.App;
        }
    }
}
