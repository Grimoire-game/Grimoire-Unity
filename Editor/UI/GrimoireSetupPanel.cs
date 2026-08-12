using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Onboarding for Grimoire Connect: sign in, then pick company and game on
    /// the same styled card as login.
    /// </summary>
    public class GrimoireSetupPanel
    {
        private struct CompanyOption
        {
            public string Id;
            public string Label;
        }

        private string _email = "";
        private string _password = "";
        private string _code = "";
        private string _tempToken;
        private string _error;
        private bool _busy;

        private GameDirectoryEntry[] _games = Array.Empty<GameDirectoryEntry>();
        private CompanyOption[] _companies = Array.Empty<CompanyOption>();
        private int _companyIndex = -1;
        private int _gameIndex = -1;
        private bool _gamesLoading;
        private bool _gamesFetchRequested;
        private string _gamesError;

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
                    return SetupPhase.SelectWorkspace;
                }

                return SetupPhase.Ready;
            }
        }

        public void Draw(Rect area)
        {
            switch (CurrentPhase)
            {
                case SetupPhase.Login:
                    DrawLoginStep(area);
                    break;
                case SetupPhase.SelectWorkspace:
                    DrawWorkspaceStep(area);
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
            _companies = Array.Empty<CompanyOption>();
            _gamesError = null;
            _gamesFetchRequested = false;
            _companyIndex = -1;
            _gameIndex = -1;
            _error = null;
        }

        public void BeginWorkspaceSelection()
        {
            GrimoireSettings.ClearWorkspace();
            _gamesFetchRequested = false;
            _companyIndex = -1;
            _gameIndex = -1;
            FetchGames();
            RepaintNeeded?.Invoke();
        }

        private void DrawLoginStep(Rect area)
        {
            var action = GrimoireLoginUi.Draw(
                area,
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
                    RepaintNeeded?.Invoke();
                    break;
                case GrimoireLoginUi.Action.ForgotPassword:
                    Application.OpenURL(GrimoireLoginUi.PlatformLoginUrl);
                    break;
            }
        }

        private void DrawWorkspaceStep(Rect area)
        {
            if (!_gamesFetchRequested && !_gamesLoading)
            {
                _gamesFetchRequested = true;
                FetchGames();
            }

            SyncWorkspaceSelection();
            var companyBeforeUi = _companyIndex;

            var companyLabels = _companies.Select(company => company.Label).ToArray();
            var filteredGames = GetGamesForSelectedCompany();
            var gameLabels = filteredGames.Select(game => game.name).ToArray();
            var gamesEnabled = _companyIndex >= 0 && _companyIndex < _companies.Length && gameLabels.Length > 0;

            var action = GrimoireLoginUi.DrawWorkspaceSelection(
                area,
                ref _companyIndex,
                ref _gameIndex,
                companyLabels,
                gameLabels,
                gamesEnabled,
                _busy,
                _gamesLoading,
                _gamesError ?? _error);

            if (_companyIndex != companyBeforeUi)
            {
                filteredGames = GetGamesForSelectedCompany();
                gameLabels = filteredGames.Select(game => game.name).ToArray();
                _gameIndex = gameLabels.Length == 1 ? 0 : -1;
            }
            else if (_companyIndex >= 0 && _companyIndex < _companies.Length &&
                     (_gameIndex < 0 || _gameIndex >= gameLabels.Length))
            {
                _gameIndex = gameLabels.Length == 1 ? 0 : -1;
            }

            switch (action)
            {
                case GrimoireLoginUi.WorkspaceAction.Continue:
                    ConfirmWorkspace(filteredGames);
                    break;
                case GrimoireLoginUi.WorkspaceAction.SignOut:
                    GrimoireAuthSession.SignOut();
                    OnSignedOut();
                    RepaintNeeded?.Invoke();
                    break;
            }
        }

        private void SyncWorkspaceSelection()
        {
            if (_companies.Length == 0)
            {
                _companyIndex = -1;
                _gameIndex = -1;
                return;
            }

            if (_companyIndex < 0 || _companyIndex >= _companies.Length)
            {
                if (!string.IsNullOrEmpty(GrimoireSettings.CompanyId))
                {
                    var savedCompanyIndex = Array.FindIndex(_companies, company => company.Id == GrimoireSettings.CompanyId);
                    _companyIndex = savedCompanyIndex >= 0 ? savedCompanyIndex : -1;
                }
                else if (_companies.Length == 1)
                {
                    // Single company: pre-select so the user only picks a game.
                    _companyIndex = 0;
                }
                else
                {
                    _companyIndex = -1;
                }
            }

            var filteredGames = GetGamesForSelectedCompany();
            if (_companyIndex < 0)
            {
                _gameIndex = -1;
                return;
            }

            if (!string.IsNullOrEmpty(GrimoireSettings.GameId))
            {
                var savedGameIndex = Array.FindIndex(filteredGames, game => game.id == GrimoireSettings.GameId);
                if (savedGameIndex >= 0)
                {
                    _gameIndex = savedGameIndex;
                    return;
                }
            }

            if (_gameIndex < 0 || _gameIndex >= filteredGames.Length)
            {
                _gameIndex = filteredGames.Length == 1 ? 0 : -1;
            }
        }

        private GameDirectoryEntry[] GetGamesForSelectedCompany()
        {
            if (_companyIndex < 0 || _companyIndex >= _companies.Length)
            {
                return Array.Empty<GameDirectoryEntry>();
            }

            var companyId = _companies[_companyIndex].Id;
            return _games
                .Where(game => game.company_id == companyId)
                .OrderBy(game => game.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private void ConfirmWorkspace(GameDirectoryEntry[] filteredGames)
        {
            if (_companyIndex < 0 || _companyIndex >= _companies.Length ||
                _gameIndex < 0 || _gameIndex >= filteredGames.Length)
            {
                _error = "Choose a company and a game to continue.";
                RepaintNeeded?.Invoke();
                return;
            }

            var company = _companies[_companyIndex];
            var game = filteredGames[_gameIndex];
            SelectWorkspace(company, game);
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
            _companies = BuildCompanyOptions(_games);
            TryMigrateSavedGameWithoutCompany();
            SyncWorkspaceSelection();

            if (_companies.Length == 1 && _games.Length == 1 && !GrimoireSettings.HasGameId)
            {
                SelectWorkspace(_companies[0], _games[0]);
                return;
            }

            RepaintNeeded?.Invoke();
        }

        /// <summary>
        /// Older plugin versions stored only game_id. Infer company from the
        /// saved game so existing projects keep working after upgrade.
        /// </summary>
        private void TryMigrateSavedGameWithoutCompany()
        {
            if (GrimoireSettings.HasCompanyId || !GrimoireSettings.HasGameId)
            {
                return;
            }

            var savedGame = _games.FirstOrDefault(game => game.id == GrimoireSettings.GameId);
            if (savedGame == null || string.IsNullOrEmpty(savedGame.company_id))
            {
                return;
            }

            var company = _companies.FirstOrDefault(entry => entry.Id == savedGame.company_id);
            if (string.IsNullOrEmpty(company.Id))
            {
                company = new CompanyOption
                {
                    Id = savedGame.company_id,
                    Label = ResolveCompanyLabel(savedGame.company_id, _games.Where(game => game.company_id == savedGame.company_id).ToArray()),
                };
            }

            GrimoireSettings.SelectCompany(company.Id, company.Label);
        }

        private static CompanyOption[] BuildCompanyOptions(GameDirectoryEntry[] games)
        {
            return games
                .Where(game => !string.IsNullOrEmpty(game.company_id))
                .GroupBy(game => game.company_id)
                .Select(group =>
                {
                    var companyGames = group.OrderBy(game => game.name, StringComparer.OrdinalIgnoreCase).ToArray();
                    return new CompanyOption
                    {
                        Id = group.Key,
                        Label = ResolveCompanyLabel(group.Key, companyGames),
                    };
                })
                .OrderBy(company => company.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string ResolveCompanyLabel(string companyId, GameDirectoryEntry[] companyGames)
        {
            var apiName = companyGames
                .Select(game => game.company_name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
            if (!string.IsNullOrWhiteSpace(apiName))
            {
                return apiName.Trim();
            }

            if (companyId == GrimoireSettings.CompanyId && !string.IsNullOrEmpty(GrimoireSettings.CompanyName))
            {
                return GrimoireSettings.CompanyName;
            }

            return $"Company ({companyGames.Length} games)";
        }

        private void SelectWorkspace(CompanyOption company, GameDirectoryEntry game)
        {
            GrimoireSettings.SelectWorkspace(company.Id, company.Label, game.id, game.name);
            GrimoireObjectKeyResolver.InvalidateCache();
            SetupCompleted?.Invoke();
            RepaintNeeded?.Invoke();
        }
    }

    public enum SetupPhase
    {
        Login,
        SelectWorkspace,
        Ready,
    }
}
