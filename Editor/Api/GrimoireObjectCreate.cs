using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grimoire.PluginV2.Editor
{
    /// <summary>
    /// Guards <c>POST /api/v1/objects</c>. Creation is immediate on the server,
    /// so this only allows a draft built from a name or a template id.
    /// </summary>
    public static class GrimoireObjectCreate
    {
        public const string DraftStatus = "draft";

        public const int MaxNameLength = 256;
        public const int MaxDescriptionLength = 2000;
        public const int MaxCodeIdLength = 200;
        public const int MaxTagLength = 64;
        public const int MaxTags = 32;

        /// <summary>Page size used when checking whether a name already exists.</summary>
        public const int NameSearchLimit = 200;

        public static bool IsUuid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return Guid.TryParseExact(value.Trim(), "D", out _);
        }

        /// <summary>
        /// Returns a payload safe to POST, or a failure that must not be sent.
        /// Status is accepted only when it is empty or <see cref="DraftStatus"/>;
        /// the returned payload always uses draft.
        /// </summary>
        public static ApiResult<CreateObjectRequest> Prepare(CreateObjectRequest request)
        {
            if (request == null)
            {
                return ApiResult<CreateObjectRequest>.Fail(
                    "Nothing to create.",
                    "missing_parameter");
            }

            var nameError = ValidateName(request.name, out var name);
            if (nameError != null)
            {
                return ApiResult<CreateObjectRequest>.Fail(nameError, "invalid_name");
            }

            var status = request.status?.Trim();
            if (!string.IsNullOrEmpty(status) &&
                !string.Equals(status, DraftStatus, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResult<CreateObjectRequest>.Fail(
                    "The Unity plugin creates objects as drafts. Publish the object in Grimoire after it is ready.",
                    "status_not_allowed");
            }

            string templateId = null;
            if (!string.IsNullOrWhiteSpace(request.template_id))
            {
                templateId = request.template_id.Trim();
                if (!IsUuid(templateId))
                {
                    return ApiResult<CreateObjectRequest>.Fail(
                        "Template id must be a UUID from Grimoire.",
                        "invalid_template");
                }
            }

            var descriptionError = ValidateDescription(request.description, out var description);
            if (descriptionError != null)
            {
                return ApiResult<CreateObjectRequest>.Fail(descriptionError, "invalid_description");
            }

            var codeError = ValidateCodeId(request.code_id, out var codeId);
            if (codeError != null)
            {
                return ApiResult<CreateObjectRequest>.Fail(codeError, "invalid_code_id");
            }

            var tagError = ValidateTags(request.tags, out var tags);
            if (tagError != null)
            {
                return ApiResult<CreateObjectRequest>.Fail(tagError, "invalid_tags");
            }

            return ApiResult<CreateObjectRequest>.Ok(new CreateObjectRequest
            {
                name = name,
                description = description,
                code_id = codeId,
                tags = tags,
                template_id = templateId,
                status = DraftStatus,
            });
        }

        /// <summary>
        /// Objects whose name equals <paramref name="name"/> (case-insensitive).
        /// <see cref="NameCollision.Incomplete"/> is true when the search page was
        /// full, so a later match might still exist.
        /// </summary>
        public static async Task<ApiResult<NameCollision>> FindExactNamesAsync(string gameId, string name)
        {
            if (!IsUuid(gameId) || string.IsNullOrWhiteSpace(name))
            {
                return ApiResult<NameCollision>.Fail(
                    "Choose a game and a name before checking for duplicates.",
                    "missing_parameter");
            }

            var page = await GrimoireApiClient.ListObjectsAsync(
                gameId.Trim(), name.Trim(), NameSearchLimit, 0);
            if (!page.Success)
            {
                return ApiResult<NameCollision>.Fail(page.Error, page.Code, page.HttpStatus);
            }

            var matches = new List<ObjectSummary>();
            var rows = page.Data ?? Array.Empty<ObjectSummary>();
            foreach (var summary in rows)
            {
                if (summary != null &&
                    string.Equals(summary.name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(summary);
                }
            }

            return ApiResult<NameCollision>.Ok(new NameCollision
            {
                Matches = matches.ToArray(),
                Incomplete = rows.Length >= NameSearchLimit,
            });
        }

        private static string ValidateName(string raw, out string name)
        {
            name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return "Enter a name.";
            }

            if (name.Length > MaxNameLength)
            {
                return $"Name must be {MaxNameLength} characters or fewer.";
            }

            if (ContainsControl(name))
            {
                return "Name cannot contain control characters.";
            }

            return null;
        }

        private static string ValidateDescription(string raw, out string description)
        {
            description = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (description == null)
            {
                return null;
            }

            if (description.Length > MaxDescriptionLength)
            {
                return $"Description must be {MaxDescriptionLength} characters or fewer.";
            }

            if (ContainsControl(description))
            {
                return "Description cannot contain control characters.";
            }

            return null;
        }

        /// <summary>
        /// Optional code id. Empty means Grimoire generates one. Segments split
        /// on <c>/</c> cannot be empty, <c>.</c>, or <c>..</c>.
        /// </summary>
        private static string ValidateCodeId(string raw, out string codeId)
        {
            codeId = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            if (codeId == null)
            {
                return null;
            }

            if (codeId.Length > MaxCodeIdLength)
            {
                return $"Code ID must be {MaxCodeIdLength} characters or fewer.";
            }

            if (codeId.IndexOf('\\') >= 0 || ContainsControl(codeId))
            {
                return "Code ID cannot contain backslashes or control characters.";
            }

            var segments = codeId.Split('/');
            if (segments.Length == 0)
            {
                return "Code ID is empty.";
            }

            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment != segment.Trim())
                {
                    return "Code ID cannot start or end with / or a space, or contain an empty segment.";
                }

                if (segment == "." || segment == "..")
                {
                    return "Code ID cannot contain . or .. segments.";
                }

                foreach (var character in segment)
                {
                    if (char.IsLetterOrDigit(character) || character == '_' || character == '-' || character == '.' || character == ' ')
                    {
                        continue;
                    }

                    return "Code ID can use letters, numbers, spaces, and _ - . / only.";
                }
            }

            return null;
        }

        private static string ValidateTags(string[] raw, out string[] tags)
        {
            tags = null;
            if (raw == null || raw.Length == 0)
            {
                return null;
            }

            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tag in raw)
            {
                var trimmed = tag?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                if (trimmed.Length > MaxTagLength)
                {
                    return $"Each tag must be {MaxTagLength} characters or fewer.";
                }

                if (trimmed.IndexOf(',') >= 0 || ContainsControl(trimmed))
                {
                    return "Tags cannot contain commas or control characters.";
                }

                if (seen.Add(trimmed))
                {
                    kept.Add(trimmed);
                }
            }

            if (kept.Count > MaxTags)
            {
                return $"Use {MaxTags} tags or fewer.";
            }

            tags = kept.Count == 0 ? null : kept.ToArray();
            return null;
        }

        private static bool ContainsControl(string value)
        {
            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public struct NameCollision
    {
        public ObjectSummary[] Matches;
        public bool Incomplete;
    }
}
