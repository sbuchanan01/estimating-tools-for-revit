using System.Windows;

namespace EstimatingTools.UI
{
    /// <summary>
    /// Popup opened from the Pricing Setup dialog's Estimate template
    /// section. Explains what a template is, lists the supported
    /// placeholder + table-anchor tokens, and points at the Generate
    /// sample button as the fastest onramp.
    ///
    /// Content lives in the XAML — this code-behind just closes the
    /// window on the Close button click.
    /// </summary>
    public partial class EstimateTemplateHelpDialog : Window
    {
        public EstimateTemplateHelpDialog()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
