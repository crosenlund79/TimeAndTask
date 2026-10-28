using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;

namespace WorkTrace.Core.Management;

/// <summary>
/// Stateless field validation. Rules that need stored data (uniqueness, existence, active state)
/// are enforced by the services on top of these checks.
/// </summary>
public static class ManagementValidator
{
    public static ValidationErrors Validate(ClientInput input)
    {
        var errors = new ValidationErrors();
        var name = Text.Clean(input.Name);
        if (name is null)
        {
            errors.Add(nameof(ClientInput.Name), "Enter a client name.");
        }
        else if (name.Length > Client.NameMaxLength)
        {
            errors.Add(nameof(ClientInput.Name), $"Use at most {Client.NameMaxLength} characters.");
        }

        if (Text.Clean(input.Description)?.Length > Client.DescriptionMaxLength)
        {
            errors.Add(nameof(ClientInput.Description), $"Use at most {Client.DescriptionMaxLength} characters.");
        }

        return errors;
    }

    public static ValidationErrors Validate(TaskInput input)
    {
        var errors = new ValidationErrors();
        if (input.ClientId is null)
        {
            errors.Add(nameof(TaskInput.ClientId), "Choose a client.");
        }

        var title = Text.Clean(input.Title);
        if (title is null)
        {
            errors.Add(nameof(TaskInput.Title), "Enter a short description of the task.");
        }
        else if (title.Length > WorkTask.TitleMaxLength)
        {
            errors.Add(nameof(TaskInput.Title), $"Use at most {WorkTask.TitleMaxLength} characters.");
        }

        if (Text.Clean(input.ExternalId)?.Length > WorkTask.ExternalIdMaxLength)
        {
            errors.Add(nameof(TaskInput.ExternalId), $"Use at most {WorkTask.ExternalIdMaxLength} characters.");
        }

        if (Text.Clean(input.Notes)?.Length > WorkTask.NotesMaxLength)
        {
            errors.Add(nameof(TaskInput.Notes), $"Use at most {WorkTask.NotesMaxLength} characters.");
        }

        return errors;
    }

    public static ValidationErrors ValidateLookupName(string? name)
    {
        var errors = new ValidationErrors();
        var clean = Text.Clean(name);
        if (clean is null)
        {
            errors.Add("Name", "Enter a name.");
        }
        else if (clean.Length > LookupItem.NameMaxLength)
        {
            errors.Add("Name", $"Use at most {LookupItem.NameMaxLength} characters.");
        }

        return errors;
    }
}
