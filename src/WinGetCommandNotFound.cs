using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
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
                var psi = new ProcessStartInfo("winget", "source update --name winget");
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
        private SqliteConnection? _dbConnection;

        public WinGetCommandNotFoundFeedback(string guid)
        {
            _guid = new Guid(guid);
            // Trying to enumerate WindowsApps folder results in AccessDenied,
            // so using a hardcoded path for now
            var dbPath = new FileInfo(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\WindowsApps\Microsoft.Winget.Source_2022.1227.1114.286_neutral__8wekyb3d8bbwe\public\index.db");
            if (!dbPath.Exists)
            {
                throw new Exception("Could not find index.db");
            }

            // open connection to index.db
            _dbConnection = new SqliteConnection("Data Source=" + dbPath);
            _dbConnection.Open();
        }

        public void Dispose()
        {
            _dbConnection?.Close();
            _dbConnection?.Dispose();
        }

        public Guid Id => _guid;

        public string Name => "winget-cmd-not-found";

        public string Description => "Finds missing commands that can be installed via winget.";

        /// <summary>
        /// Gets feedback based on the given commandline and error record.
        /// </summary>
        public string? GetFeedback(string commandLine, ErrorRecord lastError, CancellationToken token)
        {
            if (_dbConnection is not null && lastError.FullyQualifiedErrorId == "CommandNotFoundException")
            {
                var target = (string)lastError.TargetObject;
                var command = _dbConnection.CreateCommand();
                command.CommandText =
                @"
                    SELECT
                        ids.id
                    FROM
                        commands, commands_map, manifest, ids
                    WHERE
                        commands.command = $command
                        AND commands.rowid = commands_map.command
                        AND manifest.rowid = commands_map.manifest
                        AND ids.rowid = manifest.id
                    LIMIT 1
                ";
                command.Parameters.AddWithValue("$command", target);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var suggestion = "winget install " + reader.GetString(0);
                        WinGetCommandNotFoundPredictor.WingetPrediction = suggestion;
                        return suggestion;
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