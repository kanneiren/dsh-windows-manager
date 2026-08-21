using System;
using System.Threading.Tasks;

namespace DeepSeekHarnessManager
{
    public enum ConflictAction
    {
        Cancel,
        UseAlternate,
        EndProcess
    }

    public sealed class ConflictChoice
    {
        public ConflictAction Action { get; set; }
        public int Port { get; set; }
    }

    public enum ManagerMessageKind
    {
        Information,
        Warning,
        Error
    }

    public enum ManagerConfirmKind
    {
        Information,
        Question,
        Warning
    }

    /// <summary>
    /// User interaction boundary used by Manager Core. The tray frontend
    /// provides a WinForms implementation; headless or WSL scenarios can
    /// provide a non-visual implementation without changing core logic.
    /// </summary>
    public interface IManagerInteraction
    {
        void Show(ManagerMessageKind kind, string message);
        bool Confirm(ManagerConfirmKind kind, string message, string title);
        ConflictChoice ResolvePortConflict(PortInspection inspection, int alternatePort);
        ResidueRepairChoice ResolveResidueRepair(ResidueDiagnosis diagnosis, int alternatePort);
        bool ConfirmForceEnd(ProcessIdentity process);
        UpdateOutcome WaitForUpdate(string title, Task<UpdateOutcome> updateTask);
    }

    public sealed class SilentManagerInteraction : IManagerInteraction
    {
        public static readonly SilentManagerInteraction Instance = new SilentManagerInteraction();

        public void Show(ManagerMessageKind kind, string message)
        {
            if (kind == ManagerMessageKind.Error) FileLog.Error(message ?? String.Empty);
            else FileLog.Warn(message ?? String.Empty);
        }

        public bool Confirm(ManagerConfirmKind kind, string message, string title)
        {
            FileLog.Warn("Interaction confirmation was declined in silent mode: " + (message ?? String.Empty));
            return false;
        }

        public ConflictChoice ResolvePortConflict(PortInspection inspection, int alternatePort)
        {
            ConflictChoice choice = new ConflictChoice();
            choice.Action = ConflictAction.Cancel;
            choice.Port = 0;
            return choice;
        }

        public ResidueRepairChoice ResolveResidueRepair(ResidueDiagnosis diagnosis, int alternatePort)
        {
            FileLog.Warn("Residue repair was declined in silent mode: " + (diagnosis == null ? String.Empty : diagnosis.Kind.ToString()));
            return new ResidueRepairChoice { Action = ResidueRepairAction.Cancel, Port = 0 };
        }

        public bool ConfirmForceEnd(ProcessIdentity process)
        {
            FileLog.Warn("Force-end confirmation was declined in silent mode.");
            return false;
        }

        public UpdateOutcome WaitForUpdate(string title, Task<UpdateOutcome> updateTask)
        {
            if (updateTask == null) return new UpdateOutcome { Error = "The update task is unavailable." };
            try
            {
                updateTask.Wait();
                return updateTask.Result;
            }
            catch (Exception exception)
            {
                Exception inner = exception;
                while (inner is AggregateException && inner.InnerException != null) inner = inner.InnerException;
                return new UpdateOutcome { Error = inner == null ? exception.Message : inner.Message };
            }
        }
    }
}