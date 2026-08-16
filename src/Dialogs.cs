using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DeepSeekHarnessManager
{
    public static class ManagerDialogs
    {
        public static ConflictChoice ShowPortConflict(IWin32Window owner, PortInspection inspection, int alternatePort)
        {
            using (PortConflictForm form = new PortConflictForm(inspection, alternatePort))
            {
                form.ShowDialog(owner);
                return form.Choice;
            }
        }
    }

    internal sealed class PortConflictForm : Form
    {
        private readonly ConflictChoice choice;

        public PortConflictForm(PortInspection inspection, int alternatePort)
        {
            choice = new ConflictChoice();
            choice.Action = ConflictAction.Cancel;
            choice.Port = 0;
            Text = Localization.Text("Dialog.PortConflictTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(590, 300);
            Font = SystemFonts.MessageBoxFont;

            Label title = new Label();
            title.Text = Localization.Format("Dialog.PortInUse", inspection.Port);
            title.Font = new Font(Font, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(18, 18);
            Controls.Add(title);

            TextBox details = new TextBox();
            details.Location = new Point(18, 50);
            details.Size = new Size(554, 165);
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BackColor = SystemColors.Window;
            details.Text = BuildDetails(inspection);
            Controls.Add(details);

            Button alternate = new Button();
            alternate.Text = alternatePort > 0 ? Localization.Format("Dialog.UsePort", alternatePort) : Localization.Text("Dialog.NoAlternate");
            alternate.Enabled = alternatePort > 0;
            alternate.Size = new Size(145, 32);
            alternate.Location = new Point(18, 242);
            alternate.Click += delegate
            {
                choice.Action = ConflictAction.UseAlternate;
                choice.Port = alternatePort;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(alternate);

            string protectedReason;
            bool processProtected = ProcessInspector.IsProtected(inspection.Process, out protectedReason);
            Button endProcess = new Button();
            endProcess.Text = Localization.Text("Dialog.EndProcess");
            endProcess.Enabled = !processProtected;
            endProcess.Size = new Size(170, 32);
            endProcess.Location = new Point(173, 242);
            if (processProtected) endProcess.Text = Localization.Text("Dialog.ProtectedProcess");
            endProcess.Click += delegate
            {
                choice.Action = ConflictAction.EndProcess;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(endProcess);

            Button cancel = new Button();
            cancel.Text = Localization.Text("Dialog.Cancel");
            cancel.Size = new Size(100, 32);
            cancel.Location = new Point(472, 242);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = alternate;

            if (processProtected)
            {
                ToolTip tip = new ToolTip();
                tip.SetToolTip(endProcess, protectedReason);
            }
        }

        public ConflictChoice Choice { get { return choice; } }

        private static string BuildDetails(PortInspection inspection)
        {
            StringBuilder value = new StringBuilder();
            value.AppendLine(Localization.Text("Details.Port") + ": " + inspection.Port);
            value.AppendLine("PID: " + inspection.ProcessId);
            if (inspection.Process != null)
            {
                value.AppendLine(Localization.Text("Details.Process") + ": " + inspection.Process.Name);
                value.AppendLine(Localization.Text("Details.Path") + ": " + inspection.Process.ImagePath);
                if (inspection.Process.StartTimeUtc.HasValue) value.AppendLine(Localization.Text("Details.Started") + ": " + inspection.Process.StartTimeUtc.Value.ToLocalTime().ToString("G"));
                if (inspection.Process.Services != null && inspection.Process.Services.Count > 0)
                    value.AppendLine(Localization.Text("Details.Services") + ": " + String.Join(", ", inspection.Process.Services.ToArray()));
            }
            value.AppendLine();
            value.AppendLine(Localization.Text("Dialog.NeverAutoEnd"));
            return value.ToString();
        }
    }

    internal sealed class UpdateProgressForm : Form
    {
        private readonly Task<UpdateOutcome> task;
        private readonly ProgressBar progress;
        private readonly Label status;
        private readonly Timer timer;
        private UpdateOutcome result;

        private UpdateProgressForm(string title, Task<UpdateOutcome> updateTask)
        {
            task = updateTask;
            Text = title;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(480, 110);
            Font = SystemFonts.MessageBoxFont;

            status = new Label();
            status.Text = Localization.Text("Update.Progress");
            status.AutoSize = true;
            status.Location = new Point(18, 18);
            Controls.Add(status);

            progress = new ProgressBar();
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 30;
            progress.Location = new Point(18, 52);
            progress.Size = new Size(444, 24);
            Controls.Add(progress);

            timer = new Timer();
            timer.Interval = 250;
            timer.Tick += OnTick;
            timer.Start();
        }

        public static UpdateOutcome Run(IWin32Window owner, string title, Task<UpdateOutcome> task)
        {
            using (UpdateProgressForm form = new UpdateProgressForm(title, task))
            {
                form.ShowDialog(owner);
                return form.result;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        private void OnTick(object sender, EventArgs args)
        {
            if (!task.IsCompleted) return;
            timer.Stop();
            if (task.IsFaulted)
            {
                Exception exception = task.Exception == null ? null : task.Exception.GetBaseException();
                result = new UpdateOutcome { Error = exception == null ? Localization.Text("State.Error") : exception.Message };
            }
            else if (task.IsCanceled) result = new UpdateOutcome { Error = Localization.Text("Dialog.Cancel") };
            else result = task.Result;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
    public sealed class WinFormsManagerInteraction : IManagerInteraction
    {
        public void Show(ManagerMessageKind kind, string message)
        {
            MessageBoxIcon icon = kind == ManagerMessageKind.Error
                ? MessageBoxIcon.Error
                : (kind == ManagerMessageKind.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            MessageBox.Show(message, Localization.Text("App.Title"), MessageBoxButtons.OK, icon);
        }

        public bool Confirm(ManagerConfirmKind kind, string message, string title)
        {
            MessageBoxIcon icon = kind == ManagerConfirmKind.Warning
                ? MessageBoxIcon.Warning
                : (kind == ManagerConfirmKind.Information ? MessageBoxIcon.Information : MessageBoxIcon.Question);
            DialogResult result = MessageBox.Show(message, title, MessageBoxButtons.YesNo, icon);
            return result == DialogResult.Yes;
        }

        public ConflictChoice ResolvePortConflict(PortInspection inspection, int alternatePort)
        {
            return ManagerDialogs.ShowPortConflict(null, inspection, alternatePort);
        }

        public bool ConfirmForceEnd(ProcessIdentity process)
        {
            string name = process == null ? String.Empty : (process.Name ?? String.Empty);
            string path = process == null ? String.Empty : (process.ImagePath ?? String.Empty);
            int processId = process == null ? 0 : process.ProcessId;
            DialogResult result = MessageBox.Show(
                Localization.Format("Dialog.ForceEnd", processId, name, path),
                Localization.Text("Dialog.ForceEndTitle"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            return result == DialogResult.Yes;
        }

        public UpdateOutcome WaitForUpdate(string title, Task<UpdateOutcome> updateTask)
        {
            return UpdateProgressForm.Run(null, title, updateTask);
        }
    }
}
