using System.Collections.ObjectModel;
using System.Xml.Linq;

using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Platform.Data;

namespace Platform.Vault;

/// <summary>
/// Keeps the Data Protection key ring in Postgres, beside the ciphertext it protects.
/// <para>
/// Without this the ring lives under the host's own directory and a container that restarts mints a
/// fresh key — at which point every secret in <c>platform.credentials</c> is undecryptable and the
/// only symptom is checks that stop authenticating. Persisting it here means the keys and the
/// ciphertext are restored, backed up and moved as one thing.
/// </para>
/// <para>
/// Hand-written rather than taken from <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c>:
/// the interface is two methods, the package would be a new dependency for forty lines, and the
/// official one requires the DbContext to implement its own interface, which would put a Data
/// Protection concept into the shared platform context.
/// </para>
/// </summary>
public sealed class DataProtectionKeyRepository(IServiceScopeFactory scopeFactory) : IXmlRepository
{
    /// <summary>
    /// Called by the key ring, which is a singleton resolved outside any request. Its own scope,
    /// therefore: a scoped DbContext captured by a singleton is the classic way to get one connection
    /// shared by every thread in the process.
    /// </summary>
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var keys = dbContext.DataProtectionKeys.AsNoTracking()
            .Select(key => key.Xml)
            .ToList();
        return new ReadOnlyCollection<XElement>([.. keys.Select(XElement.Parse)]);
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        dbContext.DataProtectionKeys.Add(new DataProtectionKey
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        });
        dbContext.SaveChanges();
    }
}
