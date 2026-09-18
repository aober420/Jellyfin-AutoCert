# Third-party dependencies

AutoCert source is licensed under GPL-3.0-only. Jellyfin's APIs are GPL-3.0-only and are provided by the host; their assemblies are not redistributed in the install ZIP.

The binary ZIP includes the following libraries under their respective licenses:

- **Certes 3.0.4** — MIT, Certes Contributors. https://github.com/fszlin/certes
- **Portable.BouncyCastle 1.9.0** (`BouncyCastle.Crypto.dll`) — Bouncy Castle license (MIT-style), The Legion of the Bouncy Castle. https://www.bouncycastle.org/licence.html
- **Newtonsoft.Json 13.0.2** — MIT, James Newton-King. https://github.com/JamesNK/Newtonsoft.Json

The corresponding license texts are included in the `licenses` directory. Development test dependencies are restored by NuGet and are not included in the install ZIP.
