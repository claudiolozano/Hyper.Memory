# Firma Authenticode

## Requisito

Para que Windows muestre un editor verificable se necesita un certificado público de **Code Signing** emitido por una autoridad de certificación o un perfil de firma administrada equivalente. Un certificado autofirmado solo sirve para pruebas internas y no elimina las advertencias de confianza en otros equipos.

El certificado debe:

- incluir el uso mejorado `Code Signing` (`1.3.6.1.5.5.7.3.3`);
- disponer de una clave privada accesible;
- estar vigente;
- estar instalado en `CurrentUser\My` o `LocalMachine\My`.

## Compilar y firmar

Obtén la huella del certificado:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
```

Genera el entregable firmado:

```powershell
.\scripts\Build-Installer.ps1 `
    -CertificateThumbprint "HUELLA_DEL_CERTIFICADO" `
    -CertificateStore CurrentUser `
    -TimestampUrl "URL_RFC3161_DEL_PROVEEDOR"
```

El proceso firma en este orden:

1. `HyperMemory.Api.exe`.
2. `HyperMemory.Bridge.exe`.
3. Empaqueta ambos dentro del instalador.
4. Firma `HyperMemorySetup.exe`.
5. Verifica las firmas con la política Authenticode y exige sello de tiempo.

El archivo `INSTALL.txt` contiene la huella SHA-256 calculada después de firmar y el estado Authenticode.

## Verificación independiente

```powershell
Get-AuthenticodeSignature .\HyperMemorySetup.exe |
    Select-Object Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

No guardes claves privadas, contraseñas PFX ni secretos del proveedor dentro del repositorio.
