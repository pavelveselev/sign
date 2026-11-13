namespace Invest.Sign.Router.Core.Abstractions;

/// <summary>Reads signed files placed by the signing software (open question 8: access method not chosen).</summary>
public interface ISignedFileSource
{
    /// <summary>
    /// Reads the whole file. Any exception (file missing, locked, share unavailable) is a technical
    /// failure and is retried by the engine.
    /// </summary>
    Task<byte[]> ReadAsync(string fileRef, CancellationToken cancellationToken);
}
