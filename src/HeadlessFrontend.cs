using System;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    /// <summary>
    /// Single-process message loop for TrayEnabled=false. It owns no tray,
    /// menu, or notifications; Manager Core, Supervisor, Runtime Bridge, and
    /// Manager Control Protocol keep running in the same EXE.
    /// </summary>
    public sealed class HeadlessFrontend : ApplicationContext
    {
        private readonly IManagerService manager;
        private readonly string initialAction;
        private readonly Timer timer;
        private readonly Control uiSink;
        private bool initialActionHandled;

        public HeadlessFrontend(IManagerService managerService, string action)
        {
            manager = managerService;
            initialAction = action;
            uiSink = new Control();
            uiSink.CreateControl();

            manager.ExitRequested += ManagerExitRequested;

            manager.TickInstances();
            timer = new Timer();
            timer.Interval = 1000;
            timer.Tick += TimerTick;
            timer.Start();
            HandleInitialAction();
            manager.StartAutomaticUpdateChecks();
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            timer.Dispose();
            uiSink.Dispose();
            base.ExitThreadCore();
        }

        private void TimerTick(object sender, EventArgs args)
        {
            try
            {
                manager.Tick();
            }
            catch (Exception exception)
            {
                FileLog.Error(exception);
            }
        }

        private void HandleInitialAction()
        {
            if (initialActionHandled) return;
            initialActionHandled = true;
            manager.HandleInitialAction(initialAction);
        }

        private void ManagerExitRequested(object sender, EventArgs args)
        {
            ExitThread();
        }
    }
}