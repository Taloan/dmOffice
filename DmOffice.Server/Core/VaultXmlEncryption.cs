using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace DmOffice.Core;

/// <summary>
/// Packt die Schluessel des Data-Protection-Schluesselrings (keys/) mit dem Hauptschluessel ein. Ohne das
/// liessen sich mit einer Kopie von keys/ und users.json (Id und Stempel) Anmelde-Cookies fuer jedes Konto
/// faelschen - auch wenn der Hauptschluessel aus der Umgebung kommt.
/// </summary>
public sealed class VaultXmlEncryptor : IXmlEncryptor
{
    private readonly Vault _vault;
    public VaultXmlEncryptor(Vault vault) => _vault = vault;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var wrapped = _vault.Wrap(Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting)));
        return new EncryptedXmlInfo(new XElement("vaultEncrypted", new XElement("value", wrapped)), typeof(VaultXmlDecryptor));
    }
}

/// <summary>Gegenstueck zu <see cref="VaultXmlEncryptor"/>; Data Protection erzeugt es selbst ueber den ServiceProvider.</summary>
public sealed class VaultXmlDecryptor : IXmlDecryptor
{
    private readonly Vault _vault;
    public VaultXmlDecryptor(IServiceProvider services) => _vault = services.GetRequiredService<Vault>();

    public XElement Decrypt(XElement encryptedElement)
    {
        var wrapped = (string?)encryptedElement.Element("value") ?? throw new FormatException("Kein eingepackter Schlüssel.");
        byte[] plain;
        // Nach dem Umstieg von der Schluesseldatei auf DMOFFICE_MASTER_KEY: beim ersten Start noch mit dem alten Schluessel
        try { plain = _vault.Unwrap(wrapped); }
        catch (System.Security.Cryptography.CryptographicException) when (_vault.Previous != null) { plain = Vault.Unwrap(_vault.Previous, wrapped); }
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }
}
