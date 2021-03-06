﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UpdateServices.Storage;
using System.Linq;
using Microsoft.UpdateServices.Metadata;
using Microsoft.UpdateServices.WebServices.ClientSync;
using System.IO;
using Microsoft.UpdateServices.ClientSync.DataModel;
using System.Threading;
using System.ServiceModel;

namespace Microsoft.UpdateServices.ClientSync.Server
{
    /// <summary>
    /// Windows Update Server implementation. Provides updates to Windows PCs
    /// </summary>
    /// <example>
    /// <para>Attach this service to your ASP.NET service using SoapCore:</para>
    /// <code>
    /// public void ConfigureServices(IServiceCollection services)
    /// {
    ///    // Enable SoapCore; this middleware provides translation services from WCF/SOAP to Asp.net
    ///    services.AddSoapCore();
    ///    //
    ///    // Initialization data
    ///    var localMetadataSource = CompressedMetadataStore.Open(sourcePath);
    ///    var updateServiceConfiguration = Newtonsoft.Json.JsonConvert.DeserializeObject&lt;Config&gt;(
    ///        File.OpenText(serviceConfigPath).ReadToEnd());
    ///    //
    ///    // Attach the service using the initialization parameters
    ///    services.TryAddSingleton&lt;ClientSyncWebService&gt;(
    ///        new Server.ClientSyncWebService(
    ///            localMetadataSource,
    ///            updateServiceConfiguration,
    ///            // address of this server; becomes the root for update content URLs
    ///            // Windows clients connect to content URLs to download update content
    ///            // If this server is not serving content, this parameter can be null
    ///            serverAddress));
    ///    ...
    /// }
    /// public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
    /// {
    ///     app.UseSoapEndpoint&lt;ClientSyncWebService&lt;(
    ///         "/ClientWebService/client.asmx",
    ///         new BasicHttpBinding(),
    ///         SoapSerializer.XmlSerializer);
    /// }
    /// </code>
    /// </example>
    public partial class ClientSyncWebService : IClientSyncWebService
    {
        /// <summary>
        /// The local repository from where updates are served.
        /// </summary>
        public IMetadataSource MetadataSource { get; private set; }

        private ReaderWriterLockSlim MetadataSourceLock = new ReaderWriterLockSlim();

        /// <summary>
        /// Mapping of update index to its identity
        /// Update indexes are used when communicating with clients, as they are smaller that full Identities
        /// </summary>
        IReadOnlyDictionary<int, Identity> MetadataSourceIndex;

        private readonly DateTime StartTime;

        Config UpdateServiceConfiguration;

        private IEnumerable<Guid> RootUpdates;

        private IEnumerable<Guid> NonLeafUpdates;

        private IEnumerable<Guid> LeafUpdatesGuids;

        private List<Guid> SoftwareLeafUpdateGuids;

        private Dictionary<Guid, int> IdToRevisionMap;
        private Dictionary<Guid, Identity> IdToFullIdentityMap;

        private const int MaxUpdatesInResponse = 50;

        private readonly string ContentRoot;


        /// <summary>
        /// Instantiates a Windows Update server instance.
        /// </summary>
        /// <param name="updateServiceConfiguration">Update service configuration. Sent to clients when requested with GetConfig and GetConfig2</param>
        /// <param name="contentRoot">The content root to use when setting the download URL in updates metadata</param>
        public ClientSyncWebService(Config updateServiceConfiguration, string contentRoot)
        {
            ContentRoot = contentRoot;

            StartTime = DateTime.Now;

            UpdateServiceConfiguration = updateServiceConfiguration;

            ApprovedSoftwareUpdates = new HashSet<Identity>();
            ApprovedDriverUpdates = new HashSet<Identity>();
        }

        /// <summary>
        /// Sets the source of update metadata
        /// </summary>
        /// <param name="metadataSource">The source for updates metadata</param>
        public void SetMetadataSource(IMetadataSource metadataSource)
        {
            MetadataSourceLock.EnterWriteLock();

            MetadataSource = metadataSource;

            if (MetadataSource != null)
            {
                // Get leaf updates - updates that have prerequisites and no dependents
                LeafUpdatesGuids = MetadataSource.GetLeafUpdates();

                // Get non leaft updates: updates that have prerequisites and dependents
                NonLeafUpdates = MetadataSource.GetNonLeafUpdates();

                // Get root updates: updates that have no prerequisites
                RootUpdates = MetadataSource.GetRootUpdates();

                // Filter out leaf updates and only retain software ones
                var leafSoftwareUpdates = MetadataSource.UpdatesIndex.Values.OfType<SoftwareUpdate>().GroupBy(u => u.Identity.ID).Select(k => k.Key).ToHashSet();
                SoftwareLeafUpdateGuids = LeafUpdatesGuids.Where(g => leafSoftwareUpdates.Contains(g)).ToList();

                // Get the mapping of update index to identity that is used in the metadata source.
                MetadataSourceIndex = MetadataSource.GetIndex();

                var latestRevisionSelector = MetadataSourceIndex
                    .ToDictionary(k => k.Value, v => v.Key)
                    .GroupBy(p => p.Key.ID)
                    .Select(group => group.OrderBy(g => g.Key.Revision).Last());

                // Create a mapping for index to update GUID
                IdToRevisionMap = latestRevisionSelector.ToDictionary(k => k.Key.ID, v => v.Value);

                // Create a mapping from GUID to full identity
                IdToFullIdentityMap = latestRevisionSelector.ToDictionary(k => k.Key.ID, v => v.Key);
            }
            else
            {
                LeafUpdatesGuids = null;
                NonLeafUpdates = null;
                RootUpdates = null;
                SoftwareLeafUpdateGuids = null;
                MetadataSourceIndex = null;
                IdToRevisionMap = null;
                IdToFullIdentityMap = null;
            }

            MetadataSourceLock.ExitWriteLock();
        }

        /// <summary>
        /// Handle get configuration requests from clients
        /// </summary>
        /// <param name="clientConfiguration">The client configuration as received from a Windows client</param>
        /// <returns>The server configuration to be sent to a Windows client</returns>
        public Task<Config> GetConfig2Async(ClientConfiguration clientConfiguration)
        {
            return Task.FromResult(new Config()
            {
                LastChange = StartTime,
                IsRegistrationRequired = false,
                AllowedEventIds = null,
                AuthInfo = new AuthPlugInInfo[]
                {
                    new AuthPlugInInfo()
                    {
                        PlugInID = "PidValidator",
                        ServiceUrl = "",
                        Parameter = ""
                    },
                    new AuthPlugInInfo()
                    {
                        PlugInID = "Anonymous",
                        ServiceUrl = "",
                        Parameter = ""
                    }
                },
                Properties = UpdateServiceConfiguration.Properties
            });
        }

        /// <summary>
        /// Handle get configuration requests from clients
        /// </summary>
        /// <param name="protocolVersion">The version of the Windows client connecting to this server</param>
        /// <returns>The server configuration to be sent to a Windows client</returns>
        public Task<Config> GetConfigAsync(string protocolVersion)
        {
            return Task.FromResult(new Config()
            {
                LastChange = StartTime,
                IsRegistrationRequired = false,
                AllowedEventIds = null,
                AuthInfo = new AuthPlugInInfo[]
                {
                    new AuthPlugInInfo()
                    {
                        PlugInID = "PidValidator",
                        ServiceUrl = "",
                        Parameter = ""
                    },
                    new AuthPlugInInfo()
                    {
                        PlugInID = "Anonymous",
                        ServiceUrl = "",
                        Parameter = ""
                    }
                },
                Properties = UpdateServiceConfiguration.Properties
            });
        }

        /// <summary>
        /// Handle get cookie requests. All requests are all granted access and a cookie is issued.
        /// </summary>
        /// <param name="authCookies">Authorization cookies received from the client</param>
        /// <param name="oldCookie">Old cookie from client</param>
        /// <param name="lastChange"></param>
        /// <param name="currentTime"></param>
        /// <param name="protocolVersion">Client supported protocol version</param>
        /// <returns>A new cookie</returns>
        public Task<Cookie> GetCookieAsync(AuthorizationCookie[] authCookies, Cookie oldCookie, DateTime lastChange, DateTime currentTime, string protocolVersion)
        {
            return Task.FromResult(new Cookie() { Expiration = DateTime.Now.AddDays(5), EncryptedData = new byte[12] });
        }

        /// <summary>
        /// Not implemented.
        /// </summary>
        /// <param name="cookie">Not implemented</param>
        /// <param name="updateIDs">Not implemented</param>
        /// <param name="infoTypes">Not implemented</param>
        /// <param name="locales">Not implemented</param>
        /// <param name="deviceAttributes">Not implemented</param>
        /// <returns>Not implemented</returns>
        public Task<ExtendedUpdateInfo2> GetExtendedUpdateInfo2Async(Cookie cookie, UpdateIdentity[] updateIDs, XmlUpdateFragmentType[] infoTypes, string[] locales, string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        string GetCoreFragment(Identity updateIdentity)
        {
            using (var xmlStream = MetadataSource.GetUpdateMetadataStream(updateIdentity))
            using (var xmlReader = new StreamReader(xmlStream))
            {
                return UpdateXmlTransformer.GetCoreFragmentFromMetadataXml(xmlReader.ReadToEnd());
            }
        }

        string GetExtendedFragment(Identity updateIdentity)
        {
            using (var xmlStream = MetadataSource.GetUpdateMetadataStream(updateIdentity))
            using (var xmlReader = new StreamReader(xmlStream))
            {
                return UpdateXmlTransformer.GetExtendedFragmentFromMetadataXml(xmlReader.ReadToEnd());
            }
        }

        string GetLocalizedProperties(Identity updateIdentity, string[] languages)
        {
            using (var xmlStream = MetadataSource.GetUpdateMetadataStream(updateIdentity))
            using (var xmlReader = new StreamReader(xmlStream))
            {
                return UpdateXmlTransformer.GetLocalizedPropertiesFromMetadataXml(xmlReader.ReadToEnd(), languages);
            }
        }

        /// <summary>
        /// Handle requests for extended update information. The extended information is extracted from update metadata.
        /// Extended information also includes file URLs
        /// </summary>
        /// <param name="cookie">Access cookie</param>
        /// <param name="revisionIDs">Revision Ids for which to get extended information</param>
        /// <param name="infoTypes">The type of extended information requested</param>
        /// <param name="locales">The language to use when getting language dependent extended information</param>
        /// <param name="deviceAttributes">Device attributes; unused</param>
        /// <returns>Extended update information response.</returns>
        public Task<ExtendedUpdateInfo> GetExtendedUpdateInfoAsync(Cookie cookie, int[] revisionIDs, XmlUpdateFragmentType[] infoTypes, string[] locales, string deviceAttributes)
        {
            MetadataSourceLock.EnterReadLock();

            if (MetadataSource == null)
            {
                throw new FaultException();
            }

            List<Update> requestedUpdates = new List<Update>();
            foreach (var requestedRevision in revisionIDs)
            {
                if (!MetadataSourceIndex.TryGetValue(requestedRevision, out Identity id))
                {
                    throw new Exception("RevisionID not found");
                }

                if (MetadataSource.CategoriesIndex.TryGetValue(id, out Update category))
                {
                    requestedUpdates.Add(category);
                }
                else
                {
                    requestedUpdates.Add(MetadataSource.UpdatesIndex[id]);
                }
            }

            var updateDataList = new List<UpdateData>();

            if (infoTypes.Contains(XmlUpdateFragmentType.Extended))
            {
                for (int i = 0; i < requestedUpdates.Count; i++)
                {
                    updateDataList.Add(new UpdateData()
                    {
                        ID = revisionIDs[i],
                        Xml = GetExtendedFragment(requestedUpdates[i].Identity)
                    });
                }
            }
            

            if (infoTypes.Contains(XmlUpdateFragmentType.LocalizedProperties))
            {
                for (int i = 0; i < requestedUpdates.Count; i++)
                {
                    var localizedXml = GetLocalizedProperties(requestedUpdates[i].Identity, locales);

                    if (!string.IsNullOrEmpty(localizedXml))
                    {
                        updateDataList.Add(new UpdateData()
                        {
                            ID = revisionIDs[i],
                            Xml = GetLocalizedProperties(requestedUpdates[i].Identity, locales)
                        });
                    }
                }
            }

            var files = requestedUpdates.Where(u => u.HasFiles).SelectMany(u => u.Files).Distinct().ToList();
            var fileList = new List<FileLocation>();
            for (int i = 0; i < files.Count; i++)
            {
                // TODO: fix; hack; this is an internal implementation detail; must be exposed from server-server-sync library
                byte[] hashBytes = Convert.FromBase64String(files[i].Digests[0].DigestBase64);
                var cachedContentDirectoryName = string.Format("{0:X}", hashBytes.Last());

                fileList.Add(new FileLocation()
                {
                    FileDigest = Convert.FromBase64String(files[i].Urls[0].DigestBase64),
                    Url = string.IsNullOrEmpty(ContentRoot) ? files[i].Urls[0].MuUrl : $"{ContentRoot}/Content/{cachedContentDirectoryName}/{files[i].Digests[0].HexString.ToLower()}"
                });
            }

            var response = new ExtendedUpdateInfo();

            if (updateDataList.Count > 0)
            {
                response.Updates = updateDataList.ToArray();
            }
            
            if (fileList.Count > 0)
            {
                response.FileLocations = fileList.ToArray();
            }

            MetadataSourceLock.ExitReadLock();

            return Task.FromResult(response);
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="fileDigests"></param>
        /// <returns>Not implemented</returns>
        public Task<GetFileLocationsResults> GetFileLocationsAsync(Cookie cookie, byte[][] fileDigests)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Not implemented</returns>
        public Task<GetTimestampsResponse> GetTimestampsAsync(GetTimestampsRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="globalIDs"></param>
        /// <param name="deviceAttributes"></param>
        /// <returns>Not implemented</returns>
        public Task<RefreshCacheResult[]> RefreshCacheAsync(Cookie cookie, UpdateIdentity[] globalIDs, string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="computerInfo"></param>
        /// <returns>Not implemented</returns>
        public Task RegisterComputerAsync(Cookie cookie, ComputerInfo computerInfo)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Not implemented</returns>
        public Task<StartCategoryScanResponse> StartCategoryScanAsync(StartCategoryScanRequest request)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Not implemented
        /// </summary>
        /// <param name="cookie"></param>
        /// <param name="installedNonLeafUpdateIDs"></param>
        /// <param name="printerUpdateIDs"></param>
        /// <param name="deviceAttributes"></param>
        /// <returns>Not implemented</returns>
        public Task<SyncInfo> SyncPrinterCatalogAsync(Cookie cookie, int[] installedNonLeafUpdateIDs, int[] printerUpdateIDs, string deviceAttributes)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Handle requests to sync updates. A client presents the list of installed updates and detectoids and the server
        /// replies with a list of more applicable updates, if any.
        /// </summary>
        /// <param name="cookie">Access cookie</param>
        /// <param name="parameters">Request parameters: list of installed updates, list of known updates, etc.</param>
        /// <returns>SyncInfo containing updates applicable to the caller.</returns>
        public Task<SyncInfo> SyncUpdatesAsync(Cookie cookie, SyncUpdateParameters parameters)
        {
            if (parameters.SkipSoftwareSync)
            {
                return DoDriversSync(parameters);   
            }
            else
            {
                return DoSoftwareUpdateSync(parameters);
            }
        }

        

        /// <summary>
        /// Converts the a list of client supplied update indexes into a list of update identities
        /// </summary>
        /// <param name="clientIndexes">Client update indexes (ints)</param>
        /// <returns>List of update identities that correspond to the client's indexes</returns>
        private List<Identity> GetUpdateIdentitiesFromClientIndexes(int[] clientIndexes)
        {
            var updateIdentities = new List<Identity>();
            if (clientIndexes != null)
            {
                foreach (var nonLeafRevision in clientIndexes)
                {
                    if (!MetadataSourceIndex.TryGetValue(nonLeafRevision, out Identity nonLeafId))
                    {
                        throw new Exception("RevisionID not found");
                    }

                    updateIdentities.Add(nonLeafId);
                }
            }
            return updateIdentities;
        }

        /// <summary>
        /// Extract installed non-leaf updates from the response and maps them to a GUID
        /// </summary>
        /// <param name="parameters">Sync parameters</param>
        /// <returns>List of update GUIDs</returns>
        private List<Guid> GetInstalledNotLeafGuidsFromSyncParameters(SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(parameters.InstalledNonLeafUpdateIDs)
                .Select(u => u.ID)
                .ToList();
        }

        /// <summary>
        /// Extract list of other known updates from the client and maps them to a  GUID
        /// </summary>
        /// <param name="parameters">Sync parameters</param>
        /// <returns>List of update GUIDs</returns>
        private List<Guid> GetOtherCachedUpdateGuidsFromSyncParameters(SyncUpdateParameters parameters)
        {
            return GetUpdateIdentitiesFromClientIndexes(parameters.OtherCachedUpdateIDs)
                .Select(u => u.ID)
                .ToList();
        }
    }
}
