using Autodesk.Revit.UI;
using System;

namespace EstimatingTools.Revit
{
    /// <summary>
    /// Generic external event handler that allows modeless WPF dialogs to safely
    /// call back into the Revit API. Store an action via SetAction(), then call
    /// ExternalEvent.Raise() — Revit will invoke Execute() on the main thread.
    /// </summary>
    public class RevitEventHandler : IExternalEventHandler
    {
        private volatile Action<UIApplication>? _action;

        public void SetAction(Action<UIApplication> action) => _action = action;

        public void Execute(UIApplication app)
        {
            var action = _action;
            _action = null;
            action?.Invoke(app);
        }

        public string GetName() => "Estimating Tools";
    }
}
