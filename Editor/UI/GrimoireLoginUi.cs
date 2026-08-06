using UnityEditor;
using UnityEngine;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Styled login UI matching the Grimoire platform auth screen (Figma Plugins / node 6-24).
    /// </summary>
    public static class GrimoireLoginUi
    {
        public const string PlatformLoginUrl = "https://app.usegrimoire.com/login";

        public enum Action
        {
            None,
            SubmitLogin,
            SubmitVerify,
            GoBack,
            ForgotPassword,
        }

        private static readonly Color Purple = new Color(0.431f, 0.208f, 1f);       // #6E35FF
        private static readonly Color Teal = new Color(0.188f, 0.890f, 0.553f);     // #30E38D
        private static readonly Color TextPrimary = new Color(0.09f, 0.09f, 0.11f);
        private static readonly Color TextSecondary = new Color(0.42f, 0.45f, 0.50f);
        private static readonly Color InputBorder = new Color(0.82f, 0.84f, 0.87f);
        private static readonly Color ErrorBg = new Color(0.99f, 0.89f, 0.89f);
        private static readonly Color ErrorBorder = new Color(0.94f, 0.27f, 0.27f);

        private const float CardWidth = 360f;
        private const float CardPadding = 28f;
        private const float FieldHeight = 40f;
        private const float FieldSpacing = 12f;
        private const float ButtonHeight = 42f;

        private static Texture2D _gradientTexture;
        private static Texture2D _whiteTexture;
        private static Texture2D _purpleTexture;
        private static Texture2D _purpleHoverTexture;
        private static Texture2D _inputBgTexture;
        private static Texture2D _logoTexture;

        private static GUIStyle _titleStyle;
        private static GUIStyle _subtitleStyle;
        private static GUIStyle _fieldStyle;
        private static GUIStyle _primaryButtonStyle;
        private static GUIStyle _linkStyle;
        private static GUIStyle _errorStyle;
        private static GUIStyle _busyStyle;
        private static GUIStyle _ghostButtonStyle;

        public static Action Draw(
            Rect area,
            ref string email,
            ref string password,
            ref string code,
            bool isTwoFactor,
            bool busy,
            string error)
        {
            EnsureStyles();
            DrawGradientBackground(area);

            var cardHeight = isTwoFactor ? 380f : 460f;
            var cardRect = new Rect(
                area.x + (area.width - CardWidth) * 0.5f,
                area.y + Mathf.Max(24f, (area.height - cardHeight) * 0.5f),
                CardWidth,
                cardHeight);

            DrawCard(cardRect);

            var content = Inset(cardRect, CardPadding);
            var y = content.y;

            y = DrawLogo(content, y);
            y += 20f;

            if (isTwoFactor)
            {
                return DrawTwoFactorContent(content, ref y, ref code, busy, error);
            }

            return DrawCredentialsContent(content, ref y, ref email, ref password, busy, error);
        }

        private static Action DrawCredentialsContent(
            Rect content,
            ref float y,
            ref string email,
            ref string password,
            bool busy,
            string error)
        {
            y = DrawCenteredLabel(content, "Welcome back", _titleStyle, y, 28f);
            y += 20f;

            using (new EditorGUI.DisabledScope(busy))
            {
                GUI.SetNextControlName("GrimoireLoginEmail");
                email = GUI.TextField(FieldRect(content, y), email, _fieldStyle);
                DrawBorder(FieldRect(content, y), InputBorder);
                if (string.IsNullOrEmpty(email) && GUI.GetNameOfFocusedControl() != "GrimoireLoginEmail")
                {
                    DrawPlaceholder(FieldRect(content, y), "Email address");
                }

                y += FieldHeight + FieldSpacing;

                GUI.SetNextControlName("GrimoireLoginPassword");
                password = PasswordField(FieldRect(content, y), password, _fieldStyle);
                DrawBorder(FieldRect(content, y), InputBorder);
                if (string.IsNullOrEmpty(password) && GUI.GetNameOfFocusedControl() != "GrimoireLoginPassword")
                {
                    DrawPlaceholder(FieldRect(content, y), "Password");
                }
            }

            y += FieldHeight + 18f;

            var canSubmit = !busy && !string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(password);
            using (new EditorGUI.DisabledScope(!canSubmit))
            {
                if (GUI.Button(ButtonRect(content, y), busy ? "Logging in..." : "Login", _primaryButtonStyle))
                {
                    return Action.SubmitLogin;
                }
            }

            y += ButtonHeight + 10f;

            if (SubmitPressed() && canSubmit)
            {
                return Action.SubmitLogin;
            }

            y = DrawError(content, y, error);

            var linkRect = new Rect(content.x, y, content.width, 22f);
            if (GUI.Button(linkRect, "Forgot your password?", _linkStyle))
            {
                return Action.ForgotPassword;
            }

            if (busy)
            {
                DrawCenteredLabel(content, "Working...", _busyStyle, content.yMax - 24f, 18f);
            }

            return Action.None;
        }

        private static Action DrawTwoFactorContent(
            Rect content,
            ref float y,
            ref string code,
            bool busy,
            string error)
        {
            y = DrawCenteredLabel(content, "Two-factor authentication", _titleStyle, y, 28f);
            y += 8f;
            y = DrawCenteredLabel(
                content,
                "Enter the 6-digit code from your authenticator app",
                _subtitleStyle,
                y,
                40f);
            y += 16f;

            using (new EditorGUI.DisabledScope(busy))
            {
                GUI.SetNextControlName("GrimoireLoginCode");
                code = GUI.TextField(FieldRect(content, y), code, _fieldStyle);
                DrawBorder(FieldRect(content, y), InputBorder);
                if (string.IsNullOrEmpty(code) && GUI.GetNameOfFocusedControl() != "GrimoireLoginCode")
                {
                    DrawPlaceholder(FieldRect(content, y), "000000");
                }
            }

            y += FieldHeight + 16f;

            var trimmed = code?.Trim() ?? "";
            var canVerify = !busy && trimmed.Length == 6;
            using (new EditorGUI.DisabledScope(!canVerify))
            {
                if (GUI.Button(ButtonRect(content, y), busy ? "Verifying..." : "Verify", _primaryButtonStyle))
                {
                    return Action.SubmitVerify;
                }
            }

            y += ButtonHeight + 8f;

            if (SubmitPressed() && canVerify)
            {
                return Action.SubmitVerify;
            }

            y = DrawError(content, y, error);
            y += 8f;

            if (GUI.Button(ButtonRect(content, y), "Back to login", _ghostButtonStyle))
            {
                return Action.GoBack;
            }

            return Action.None;
        }

        private static void DrawGradientBackground(Rect area)
        {
            GUI.DrawTexture(area, GradientTexture, ScaleMode.StretchToFill);
        }

        private static void DrawCard(Rect cardRect)
        {
            var shadow = cardRect;
            shadow.x += 0f;
            shadow.y += 6f;
            shadow.height += 2f;
            EditorGUI.DrawRect(shadow, new Color(0f, 0f, 0f, 0.12f));

            GUI.DrawTexture(cardRect, _whiteTexture, ScaleMode.StretchToFill);
        }

        private static float DrawLogo(Rect content, float y)
        {
            var logo = LogoTexture;
            if (logo == null)
            {
                return DrawCenteredLabel(content, "Grimoire", _titleStyle, y, 36f);
            }

            var logoWidth = content.width * 0.72f;
            var aspect = logo.height / (float)logo.width;
            var logoHeight = logoWidth * aspect;
            var logoRect = new Rect(content.x + (content.width - logoWidth) * 0.5f, y, logoWidth, logoHeight);
            GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit);
            return y + logoHeight;
        }

        private static float DrawCenteredLabel(Rect content, string text, GUIStyle style, float y, float height)
        {
            var rect = new Rect(content.x, y, content.width, height);
            GUI.Label(rect, text, style);
            return y + height;
        }

        private static float DrawError(Rect content, float y, string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return y;
            }

            var rect = new Rect(content.x, y, content.width, 0f);
            var size = _errorStyle.CalcHeight(new GUIContent(error), content.width);
            rect.height = size + 12f;
            EditorGUI.DrawRect(rect, ErrorBg);
            DrawBorder(rect, ErrorBorder);
            GUI.Label(Inset(rect, 6f), error, _errorStyle);
            return y + rect.height + 8f;
        }

        private static void DrawPlaceholder(Rect rect, string text)
        {
            var placeholderStyle = new GUIStyle(_fieldStyle)
            {
                normal = { textColor = TextSecondary },
                focused = { textColor = TextSecondary },
                hover = { textColor = TextSecondary },
            };
            GUI.Label(Inset(rect, 12f, 0f), text, placeholderStyle);
        }

        private static Rect FieldRect(Rect content, float y) =>
            new Rect(content.x, y, content.width, FieldHeight);

        private static Rect ButtonRect(Rect content, float y) =>
            new Rect(content.x, y, content.width, ButtonHeight);

        private static Rect Inset(Rect rect, float padding) =>
            new Rect(rect.x + padding, rect.y + padding, rect.width - padding * 2f, rect.height - padding * 2f);

        private static Rect Inset(Rect rect, float horizontal, float vertical) =>
            new Rect(rect.x + horizontal, rect.y + vertical, rect.width - horizontal * 2f, rect.height - vertical * 2f);

        private static void DrawBorder(Rect rect, Color color, float thickness = 1f)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thickness, rect.height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        private static string PasswordField(Rect rect, string password, GUIStyle style)
        {
            return GUI.PasswordField(rect, password, '*', style);
        }

        private static bool SubmitPressed()
        {
            var current = Event.current;
            return current.type == EventType.KeyDown &&
                   (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter);
        }

        private static Texture2D LogoTexture
        {
            get
            {
                if (_logoTexture != null)
                {
                    return _logoTexture;
                }

                var guids = AssetDatabase.FindAssets("Grimoire_Woordmerk-Paars t:Texture2D");
                if (guids.Length == 0)
                {
                    return null;
                }

                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                return _logoTexture;
            }
        }

        private static Texture2D GradientTexture
        {
            get
            {
                if (_gradientTexture != null)
                {
                    return _gradientTexture;
                }

                const int size = 256;
                _gradientTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };

                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var along = (x * 0.898f + y * 0.440f) / size;
                        along = Mathf.Clamp01(along / 0.5416f);
                        _gradientTexture.SetPixel(x, y, Color.Lerp(Purple, Teal, along));
                    }
                }

                _gradientTexture.Apply();
                return _gradientTexture;
            }
        }

        private static void EnsureStyles()
        {
            _whiteTexture ??= MakeSolidTexture(Color.white);
            _purpleTexture ??= MakeSolidTexture(Purple);
            _purpleHoverTexture ??= MakeSolidTexture(Purple * 1.08f);
            _inputBgTexture ??= MakeSolidTexture(Color.white);

            if (_titleStyle != null)
            {
                return;
            }

            _titleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                wordWrap = true,
                normal = { textColor = TextPrimary },
            };

            _subtitleStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = TextSecondary },
            };

            _fieldStyle = new GUIStyle(EditorStyles.textField)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 13,
                fixedHeight = FieldHeight,
                padding = new RectOffset(12, 12, 10, 10),
                normal =
                {
                    background = _inputBgTexture,
                    textColor = TextPrimary,
                },
                focused =
                {
                    background = _inputBgTexture,
                    textColor = TextPrimary,
                },
                hover =
                {
                    background = _inputBgTexture,
                    textColor = TextPrimary,
                },
            };

            _primaryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                fixedHeight = ButtonHeight,
                padding = new RectOffset(12, 12, 10, 10),
                normal =
                {
                    background = _purpleTexture,
                    textColor = Color.white,
                },
                hover =
                {
                    background = _purpleHoverTexture,
                    textColor = Color.white,
                },
                active =
                {
                    background = _purpleHoverTexture,
                    textColor = Color.white,
                },
                focused =
                {
                    background = _purpleTexture,
                    textColor = Color.white,
                },
            };

            _linkStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                normal = { textColor = Purple },
                hover = { textColor = Purple * 1.1f },
            };

            _ghostButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fixedHeight = 34f,
                normal =
                {
                    background = _whiteTexture,
                    textColor = TextSecondary,
                },
                hover =
                {
                    background = _whiteTexture,
                    textColor = TextPrimary,
                },
            };

            _errorStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                wordWrap = true,
                normal = { textColor = new Color(0.67f, 0.12f, 0.12f) },
            };

            _busyStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
            {
                fontSize = 11,
                normal = { textColor = TextSecondary },
            };
        }

        private static Texture2D MakeSolidTexture(Color color)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,
            };

            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply();
            return texture;
        }
    }
}
