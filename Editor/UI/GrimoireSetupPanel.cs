using System;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Onboarding flow for the Object Widget: sign in to Grimoire, then pick
    /// which game this Unity project should talk to. Mirrors the platform's
    /// login → game selection experience.
    /// </summary>
    public class GrimoireSetupPanel
    {
        private string _email = "";
        private string _password = "";
        private string _code = "";
        private string _tempToken;
        private string _error;
        private bool _busy;

        private GameDirectoryEntry[] _games = Array.Empty<GameDirectoryEntry>();
        private bool _gamesLoading;
        private bool _gamesFetchRequested;
        private string _gamesError;
        private Vector2 _gameScroll;

        public event Action SetupCompleted;
        public event Action RepaintNeeded;

        public GrimoireSetupPanel()
        {
            _email = GrimoireSettings.SessionUserEmail;
        }

        public bool NeedsSetup => !GrimoireSettings.IsConfigured;

        public SetupPhase CurrentPhase
        {
            get
            {
                if (!GrimoireAuthSession.IsSignedIn)
                {
                    return SetupPhase.Login;
                }

                if (!GrimoireSettings.HasGameId)
                {
                    return SetupPhase.SelectGame;
                }

                return SetupPhase.Ready;
            }
        }

        public void Draw()
        {
            switch (CurrentPhase)
            {
                case SetupPhase.Login:
                    DrawLoginStep();
                    break;
                case SetupPhase.SelectGame:
                    DrawGameSelectionStep();
                    break;
            }
        }

        public void OnSignedIn()
        {
            _tempToken = null;
            _password = "";
            _code = "";
            _error = null;
            _gamesFetchRequested = false;
            FetchGames();
        }

        public void OnSignedOut()
        {
            _games = Array.Empty<GameDirectoryEntry>();
            _gamesError = null;
            _gamesFetchRequested = false;
            _error = null;
        }

        public void BeginGameSelection()
        {
            GrimoireSettings.ClearGame();
            _gamesFetchRequested = false;
            FetchGames();
            RepaintNeeded?.Invoke();
        }

        private void DrawLoginStep()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Sign in to Grimoire", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Use your Grimoire account to connect this Unity project to your games.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (_tempToken == null)
                {
                    DrawCredentialsForm();
                }
                else
                {
                    DrawTwoFactorForm();
                }
            }

            if (_busy)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Working...", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(_error))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            }
        }

        private void DrawCredentialsForm()
        {
            _email = EditorGUILayout.TextField("Email", _email);
            _password = EditorGUILayout.PasswordField("Password", _password);

            EditorGUILayout.Space(8);

            var canSubmit = !string.IsNullOrWhiteSpace(_email) && !string.IsNullOrEmpty(_password);
            using (new EditorGUI.DisabledScope(!canSubmit))
            {
                if (GUILayout.Button("Sign in", GUILayout.Height(28)) || (canSubmit && SubmitPressed()))
                {
                    Login();
                }
            }
        }

        private void DrawTwoFactorForm()
        {
            EditorGUILayout.LabelField(
                "Enter the 6-digit code from your authenticator app (or a recovery code).",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            _code = EditorGUILayout.TextField("Code", _code);

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Back"))
                {
                    _tempToken = null;
                    _code = "";
                    _error = null;
                    RepaintNeeded?.Invoke();
                    return;
                }

                var canVerify = !string.IsNullOrWhiteSpace(_code) && _code.Trim().Length == 6;
                using (new EditorGUI.DisabledScope(!canVerify))
                {
                    if (GUILayout.Button("Verify", GUILayout.Height(24)) || (canVerify && SubmitPressed()))
                    {
                        Verify();
                    }
                }
            }
        }

        private void DrawGameSelectionStep()
        {
            if (!_gamesFetchRequested && !_gamesLoading)
            {
                _gamesFetchRequested = true;
                FetchGames();
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Select a game", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Signed in as {GrimoireAuthSession.UserName}. Choose which Grimoire game this Unity project should use.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(8);

            if (_gamesLoading)
            {
                EditorGUILayout.LabelField("Loading games...", EditorStyles.centeredGreyMiniLabel);
            }

            if (!string.IsNullOrEmpty(_gamesError))
            {
                EditorGUILayout.HelpBox(_gamesError, MessageType.Error);
                if (GUILayout.Button("Retry"))
                {
                    _gamesFetchRequested = false;
                    FetchGames();
                }
            }

            _gameScroll = EditorGUILayout.BeginScrollView(_gameScroll);

            foreach (var game in _games)
            {
                DrawGameRow(game);
            }

            if (!_gamesLoading && _games.Length == 0 && string.IsNullOrEmpty(_gamesError))
            {
                EditorGUILayout.HelpBox(
                    "No games were found for your account. Ask a company admin to add you to a game in Grimoire.",
                    MessageType.Info);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Sign out", GUILayout.Width(80)))
                {
                    GrimoireAuthSession.SignOut();
                    OnSignedOut();
                    RepaintNeeded?.Invoke();
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Refresh list", GUILayout.Width(90)))
                {
                    _gamesFetchRequested = false;
                    FetchGames();
                }
            }
        }

        private void DrawGameRow(GameDirectoryEntry game)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(game.name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(game.id, EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            if (GUILayout.Button("Select", GUILayout.Width(70), GUILayout.Height(32)))
            {
                SelectGame(game);
            }

            EditorGUILayout.EndHorizontal();
        }

        private async void Login()
        {
            _busy = true;
            _error = null;
            RepaintNeeded?.Invoke();

            var result = await GrimoireApiClient.LoginAsync(_email.Trim(), _password);
            _busy = false;

            if (!result.Success)
            {
                _error = result.Error;
                RepaintNeeded?.Invoke();
                return;
            }

            if (result.Data.requires_2fa)
            {
                _tempToken = result.Data.temp_token;
                _code = "";
                RepaintNeeded?.Invoke();
                return;
            }

            CompleteSignIn(result.Data);
        }

        private async void Verify()
        {
            _busy = true;
            _error = null;
            RepaintNeeded?.Invoke();

            var result = await GrimoireApiClient.Verify2faAsync(_tempToken, _code.Trim());
            _busy = false;

            if (!result.Success)
            {
                _error = result.Error;
                RepaintNeeded?.Invoke();
                return;
            }

            CompleteSignIn(result.Data);
        }

        private void CompleteSignIn(AuthSessionData session)
        {
            if (string.IsNullOrEmpty(session?.token))
            {
                _error = "The server did not return a session token.";
                RepaintNeeded?.Invoke();
                return;
            }

            GrimoireAuthSession.ApplySession(session);
            OnSignedIn();
            RepaintNeeded?.Invoke();
        }

        private async void FetchGames()
        {
            _gamesLoading = true;
            _gamesError = null;
            RepaintNeeded?.Invoke();

            var hasSession = await GrimoireAuthSession.EnsureFreshTokenAsync();
            if (!hasSession)
            {
                _gamesLoading = false;
                _gamesError = "Your session expired. Sign in again.";
                RepaintNeeded?.Invoke();
                return;
            }

            var result = await GrimoireApiClient.ListGamesAsync();
            _gamesLoading = false;

            if (!result.Success)
            {
                _gamesError = result.Error;
                RepaintNeeded?.Invoke();
                return;
            }

            _games = result.Data ?? Array.Empty<GameDirectoryEntry>();

            if (_games.Length == 1 && !GrimoireSettings.HasGameId)
            {
                SelectGame(_games[0]);
                return;
            }

            RepaintNeeded?.Invoke();
        }

        private void SelectGame(GameDirectoryEntry game)
        {
            GrimoireSettings.SelectGame(game.id, game.name);
            GrimoireObjectKeyResolver.InvalidateCache();
            SetupCompleted?.Invoke();
            RepaintNeeded?.Invoke();
        }

        private static bool SubmitPressed()
        {
            var current = Event.current;
            return current.type == EventType.KeyDown &&
                   (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
        }
    }

    public enum SetupPhase
    {
        Login,
        SelectGame,
        Ready,
    }
}
