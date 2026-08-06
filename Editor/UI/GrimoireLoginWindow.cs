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
            window.minSize = new Vector2(420, 520);
            window.maxSize = new Vector2(900, 900);
            window._email = GrimoireSettings.SessionUserEmail;
        }

        private void OnGUI()
        {
            var action = GrimoireLoginUi.Draw(
                new Rect(0f, 0f, position.width, position.height),
                ref _email,
                ref _password,
                ref _code,
                _tempToken != null,
                _busy,
                _error);

            switch (action)
            {
                case GrimoireLoginUi.Action.SubmitLogin:
                    Login();
                    break;
                case GrimoireLoginUi.Action.SubmitVerify:
                    Verify();
                    break;
                case GrimoireLoginUi.Action.GoBack:
                    _tempToken = null;
                    _code = "";
                    _error = null;
                    Repaint();
                    break;
                case GrimoireLoginUi.Action.ForgotPassword:
                    Application.OpenURL(GrimoireLoginUi.PlatformLoginUrl);
                    break;
            }
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

            if (!GrimoireSettings.IsConfigured)
            {
                GrimoireConnectWindow.Open();
                var connect = EditorWindow.GetWindow<GrimoireConnectWindow>();
                connect.Repaint();
            }
        }
    }
}
