namespace Application.Interfaces;

/// <summary>Who is doing the current work (for the audit log). The API answers from the access token; the collector has no user.</summary>
public interface ICurrentUser
{
    string Name { get; }
}
