using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem;
using System.Management.Automation.Subsystem.Feedback;
using System.Management.Automation.Subsystem.Prediction;
using System.Threading.Tasks;

namespace wingetprovider
{
    public sealed class Init : IModuleAssemblyInitializer, IModuleAssemblyCleanup
    {
        private const string feedbackId = "e5351aa4-dfde-4d4d-bf0f-1a2f5a37d8d6";
        private const string predictorId = "b0fcf338-b1d8-43f6-bcb9-aadf697b9706";

        public void OnImport()
        {
            if (!Platform.IsWindows)
            {
                return;
            }

            using (var rs = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault()))
            {
                rs.Open();
                var invocation = rs.SessionStateProxy.InvokeCommand;
                var winget = invocation.GetCommand("winget", CommandTypes.Application);
                if (winget is null)
                {
                    return;
                }
            }

            // make sure latest index.db is loaded
            var task = Task.Run(() => 
            {
                var psi = new ProcessStartInfo("winget", "source update");
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Process.Start(psi);
            });

            SubsystemManager.RegisterSubsystem<IFeedbackProvider, WinGetCommandNotFoundFeedback>(new WinGetCommandNotFoundFeedback(feedbackId));
            SubsystemManager.RegisterSubsystem<ICommandPredictor, WinGetCommandNotFoundPredictor>(new WinGetCommandNotFoundPredictor(predictorId));
        }

        public void OnRemove(PSModuleInfo psModuleInfo)
        {
            SubsystemManager.UnregisterSubsystem<IFeedbackProvider>(new Guid(feedbackId));
            SubsystemManager.UnregisterSubsystem<ICommandPredictor>(new Guid(predictorId));
        }
    }

    public sealed class WinGetCommandNotFoundFeedback : IFeedbackProvider
    {
        private readonly Guid _guid;

        public WinGetCommandNotFoundFeedback(string guid)
        {
            _guid = new Guid(guid);
        }

        public Guid Id => _guid;

        public string Name => "winget-cmd-not-found";

        public string Description => "Finds missing commands that can be installed via winget.";

        /// <summary>
        /// Gets feedback based on the given commandline and error record.
        /// </summary>
        public string? GetFeedback(string commandLine, ErrorRecord lastError, CancellationToken token)
        {
            if (lastError.FullyQualifiedErrorId == "CommandNotFoundException")
            {
                var target = (string)lastError.TargetObject;

                if (target == "kubectl")
                {
                    return "winget install kubernetes-cli";
                }
 
                // would be better to use SQL queries against the index.db SQLite database,
                // but this is just a proof of concept to demonstrate the user experience
                var psi = new ProcessStartInfo("winget", "search --command " + target);
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var p = Process.Start(psi);
                var output = p?.StandardOutput.ReadToEnd();
                p?.WaitForExit();
                if (p?.ExitCode == 0 && output is not null && output.Length > 0)
                {
                    var lines = output.Split('\n');
                    if (lines.Length > 2)
                    {
                        var line = lines[2];
                        if (line.Length > 0)
                        {
                            var parts = line.Split(' ', 3);
                            if (parts.Length == 3)
                            {
                                string suggestion = "winget install " + parts[1];
                                WinGetCommandNotFoundPredictor.WingetPrediction = suggestion;
                                return suggestion;
                            }
                        }
                    }
                }
            }

            return null;
        }
    }

    public class WinGetCommandNotFoundPredictor : ICommandPredictor
    {
        private readonly Guid _guid;
        internal static string? _wingetPrediction;
        private static object _lock = new object();

        internal static string? WingetPrediction
        {
            get {
                lock (_lock)
                {
                    return _wingetPrediction;
                }
            }
            set {
                lock (_lock)
                {
                    _wingetPrediction = value;
                }
            }
        }

        public WinGetCommandNotFoundPredictor(string guid)
        {
            _guid = new Guid(guid);
        }

        public Guid Id => _guid;

        public string Name => "winget-cmd-not-found-predictor";

        public string Description => "Predict the install command for missing commands via winget.";

        public bool CanAcceptFeedback(PredictionClient client, PredictorFeedbackKind feedback)
        {
            return feedback switch
            {
                PredictorFeedbackKind.CommandLineAccepted => true,
                _ => false,
            };
        }

        public SuggestionPackage GetSuggestion(PredictionClient client, PredictionContext context, CancellationToken cancellationToken)
        {
            List<PredictiveSuggestion>? result = null;

            result ??= new List<PredictiveSuggestion>(1);
            if (WingetPrediction is null)
            {
                return default;
            }

            result.Add(new PredictiveSuggestion(WingetPrediction));

            if (result is not null)
            {
                return new SuggestionPackage(result);
            }

            return default;
        }

        public void OnCommandLineAccepted(PredictionClient client, IReadOnlyList<string> history)
        {
            WingetPrediction = null;
        }

        public void OnSuggestionDisplayed(PredictionClient client, uint session, int countOrIndex) { }

        public void OnSuggestionAccepted(PredictionClient client, uint session, string acceptedSuggestion) { }

        public void OnCommandLineExecuted(PredictionClient client, string commandLine, bool success) { }
    }
}