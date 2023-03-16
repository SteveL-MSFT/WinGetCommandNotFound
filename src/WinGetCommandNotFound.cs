using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem;
using System.Management.Automation.Subsystem.Feedback;
using System.Management.Automation.Subsystem.Prediction;

namespace wingetprovider
{
    public sealed class Init : IModuleAssemblyInitializer, IModuleAssemblyCleanup
    {
        internal const string id = "e5351aa4-dfde-4d4d-bf0f-1a2f5a37d8d6";

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

            SubsystemManager.RegisterSubsystem<IFeedbackProvider, WinGetCommandNotFoundFeedbackPredictor>(WinGetCommandNotFoundFeedbackPredictor.Singleton);
            SubsystemManager.RegisterSubsystem<ICommandPredictor, WinGetCommandNotFoundFeedbackPredictor>(WinGetCommandNotFoundFeedbackPredictor.Singleton);
        }

        public void OnRemove(PSModuleInfo psModuleInfo)
        {
            SubsystemManager.UnregisterSubsystem<IFeedbackProvider>(new Guid(id));
            SubsystemManager.UnregisterSubsystem<ICommandPredictor>(new Guid(id));
        }
    }

    public sealed class WinGetCommandNotFoundFeedbackPredictor : IFeedbackProvider, ICommandPredictor
    {
        private readonly Guid _guid;
        private SqliteConnection? _dbConnection;
        private string? _suggestion;
        Dictionary<string, string>? ISubsystem.FunctionsToDefine => null;

        public static WinGetCommandNotFoundFeedbackPredictor Singleton { get; } = new WinGetCommandNotFoundFeedbackPredictor(Init.id);
        private WinGetCommandNotFoundFeedbackPredictor(string guid)
        {
            _guid = new Guid(guid);
            // Trying to enumerate WindowsApps folder results in AccessDenied,
            // so using a hardcoded path for now
            var dbPath = new FileInfo(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\WindowsApps\Microsoft.Winget.Source_2023.316.2329.417_neutral__8wekyb3d8bbwe\Public\index.db");
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
        public FeedbackItem? GetFeedback(string commandLine, ErrorRecord lastError, CancellationToken token)
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
                        _suggestion = "winget install " + reader.GetString(0);
                        return new FeedbackItem(
                            Name,
                            new List<string> { _suggestion }
                        );
                    }
                }
            }

            return null;
        }

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
            if (_suggestion is null)
            {
                return default;
            }

            result.Add(new PredictiveSuggestion(_suggestion));

            if (result is not null)
            {
                return new SuggestionPackage(result);
            }

            return default;
        }

        public void OnCommandLineAccepted(PredictionClient client, IReadOnlyList<string> history)
        {
            _suggestion = null;
        }

        public void OnSuggestionDisplayed(PredictionClient client, uint session, int countOrIndex) { }

        public void OnSuggestionAccepted(PredictionClient client, uint session, string acceptedSuggestion) { }

        public void OnCommandLineExecuted(PredictionClient client, string commandLine, bool success) { }
    }
}
