# Microsoft.UpdateServices Client Sync

This library provides a C# implementation (.NET Core) of the Microsoft Update Client-Server sync protocol. The server implementation enables deploying Microsoft updates to Windows PCs and is compatible with the Windows Update client included with the Windows operating system.


The server implementation is cross platform and can be used on all platforms where .NET Core is avaialble. It can also be run as a ASP.NET web app in the cloud.


To fetch update metadata and content for deploying with this library, please see the [documentation for the Microsoft Update Server-Server sync protocol](https://microsoft.github.io/update-server-server-sync) and the [GitHub repo](https://github.com/microsoft/update-server-server-sync)


To get started, see [how to run updates services in a ASP.NET web app](examples/default_startup.html)


See [MS-WUSP](https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-wusp/b8a2ad1d-11c4-4b64-a2cc-12771fcb079b) for the complete technical documentation of the protocol.
