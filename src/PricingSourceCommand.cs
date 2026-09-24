using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using EstimatingTools.UI;

namespace EstimatingTools
{
    [Transaction(TransactionMode.Manual)]
    public class PricingSourceCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              Autodesk.Revit.DB.ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            if (uiDoc == null)
            {
                TaskDialog.Show("Pricing Setup", "No active document.");
                return Result.Cancelled;
            }
            var dialog = new PricingSourceDialog(uiDoc.Document);
            dialog.ShowDialog();
            return Result.Succeeded;
        }
    }
}
