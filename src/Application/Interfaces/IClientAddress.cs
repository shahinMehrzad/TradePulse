namespace Application.Interfaces;

/// <summary>The address of whoever is making the request (the visitor's, behind the proxy), so a wrong password can be held against it.</summary>
public interface IClientAddress
{
    string Address { get; }
}
