using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Sign-in dialog for the Grimoire user session. Handles the plain
    /// email/password flow and the 2FA challenge (`requires_2fa` +
    /// `temp_token`) from POST /api/v1/auth/login.
    /// </summary>
    public class GrimoireLoginWindow : EditorWindow
    {
        private string _email = "";
        private string _password = "";
        private string _code = "";
        private string _tempToken;
        private string _error;
        private bool _busy;

        public static void Open()
        {
            var window = GetWindow<GrimoireLoginWindow>(true, "Sign in to Grimoire", true);
            window.minSize = new Vector2(340, 190);
            window.maxSize = new Vector2(480, 240);
            window._email = GrimoireSettings.SessionUserEmail;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(_busy))
            {
                if (_tempToken == null)
                {
                    DrawCredentialsStep();
                }
                else
                {
                    DrawTwoFactorStep();
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

        private void DrawCredentialsStep()
        {
            EditorGUILayout.LabelField("Sign in with your Grimoire account to update tasks.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(4);

            _email = EditorGUILayout.TextField("Email", _email);
            _password = EditorGUILayout.PasswordField("Password", _password);

            EditorGUILayout.Space(8);

            var canSubmit = !string.IsNullOrWhiteSpace(_email) && !string.IsNullOrEmpty(_password);
            using (new EditorGUI.DisabledScope(!canSubmit))
            {
                if (GUILayout.Button("Sign in", GUILayout.Height(26)) || (canSubmit && SubmitPressed()))
                {
                    Login();
                }
            }
        }

        private void DrawTwoFactorStep()
        {
            EditorGUILayout.LabelField(
                "Two-factor authentication is enabled on this account. Enter the 6-digit code from your authenticator app (or a recovery code).",
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
                    return;
                }

                var canVerify = !string.IsNullOrWhiteSpace(_code) && _code.Trim().Length == 6;
                using (new EditorGUI.DisabledScope(!canVerify))
                {
                    if (GUILayout.Button("Verify", GUILayout.Height(22)) || (canVerify && SubmitPressed()))
                    {
                        Verify();
                    }
                }
            }
        }

        private static bool SubmitPressed()
        {
            var current = Event.current;
            return current.type == EventType.KeyDown &&
                   (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
        }

        private async void Login()
        {
            _busy = true;
            _error = null;
            Repaint();

            var result = await GrimoireApiClient.LoginAsync(_email.Trim(), _password);
            _busy = false;

            if (!result.Success)
            {
                _error = result.Error;
                Repaint();
                return;
            }

            if (result.Data.requires_2fa)
            {
                _tempToken = result.Data.temp_token;
                _code = "";
                Repaint();
                return;
            }

            CompleteSignIn(result.Data);
        }

        private async void Verify()
        {
            _busy = true;
            _error = null;
            Repaint();

            var result = await GrimoireApiClient.Verify2faAsync(_tempToken, _code.Trim());
            _busy = false;

            if (!result.Success)
            {
                _error = result.Error;
                Repaint();
                return;
            }

            CompleteSignIn(result.Data);
        }

        private void CompleteSignIn(AuthSessionData session)
        {
            if (string.IsNullOrEmpty(session?.token))
            {
                _error = "The server did not return a session token.";
                Repaint();
                return;
            }

            GrimoireAuthSession.ApplySession(session);
            _password = "";
            Close();
        }
    }
}
