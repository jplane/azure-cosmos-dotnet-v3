﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Encryption
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Encryption.Cryptography;

    internal sealed class EncryptionSettings
    {
        // TODO: Good to have constants available in the Cosmos SDK. Tracked via https://github.com/Azure/azure-cosmos-dotnet-v3/issues/2431
        internal const string IntendedCollectionHeader = "x-ms-cosmos-intended-collection-rid";

        internal const string IsClientEncryptedHeader = "x-ms-cosmos-is-client-encrypted";

        private readonly Dictionary<string, EncryptionSettingForProperty> encryptionSettingsDictByPropertyName;

        public string ContainerRidValue { get; }

        public IEnumerable<string> PropertiesToEncrypt { get; }

        public EncryptionSettingForProperty GetEncryptionSettingForProperty(string propertyName)
        {
            this.encryptionSettingsDictByPropertyName.TryGetValue(propertyName, out EncryptionSettingForProperty encryptionSettingsForProperty);

            return encryptionSettingsForProperty;
        }

        public void SetRequestHeaders(RequestOptions requestOptions)
        {
            requestOptions.AddRequestHeaders = (headers) =>
            {
                headers.Add(IsClientEncryptedHeader, bool.TrueString);
                headers.Add(IntendedCollectionHeader, this.ContainerRidValue);
            };
        }

        private EncryptionSettings(string containerRidValue)
        {
            this.ContainerRidValue = containerRidValue;
            this.encryptionSettingsDictByPropertyName = new Dictionary<string, EncryptionSettingForProperty>();
            this.PropertiesToEncrypt = this.encryptionSettingsDictByPropertyName.Keys;
        }

        private static EncryptionType GetEncryptionTypeForProperty(ClientEncryptionIncludedPath clientEncryptionIncludedPath)
        {
            return clientEncryptionIncludedPath.EncryptionType switch
            {
                CosmosEncryptionType.Deterministic => EncryptionType.Deterministic,
                CosmosEncryptionType.Randomized => EncryptionType.Randomized,
                _ => throw new ArgumentException($"Invalid encryption type {clientEncryptionIncludedPath.EncryptionType}. Please refer to https://aka.ms/CosmosClientEncryption for more details. "),
            };
        }

        public static async Task<EncryptionSettings> CreateAsync(
            ContainerProperties containerProperties,
            IEncryptionKeyCache encryptionKeyCache,
            EncryptionKeyStoreProvider encryptionKeyStoreProvider,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Debug.Assert(containerProperties != null, "ReadContainerAsync request has failed as part of InitializeEncryptionSettingsAsync operation. ");

            // set the Database Rid.
            string databaseRidValue = containerProperties.SelfLink.Split('/').ElementAt(1);

            // set the Container Rid.
            string containerRidValue = containerProperties.SelfLink.Split('/').ElementAt(3);

            // set the ClientEncryptionPolicy for the Settings.
            ClientEncryptionPolicy clientEncryptionPolicy = containerProperties.ClientEncryptionPolicy;

            EncryptionSettings encryptionSettings = new EncryptionSettings(containerRidValue);

            if (clientEncryptionPolicy != null)
            {
                if (clientEncryptionPolicy.PolicyFormatVersion > Constants.SupportedClientEncryptionPolicyFormatVersion)
                {
                    throw new InvalidOperationException("This version of Microsoft.Azure.Cosmos.Encryption cannot be used with this container." +
                        " Please upgrade to the latest version of the same. Please refer to https://aka.ms/CosmosClientEncryption for more details. ");
                }

                // for each of the unique keys in the policy Add it in /Update the cache.
                foreach (string clientEncryptionKeyId in clientEncryptionPolicy.IncludedPaths.Select(x => x.ClientEncryptionKeyId).Distinct())
                {
                    await encryptionKeyCache.GetClientEncryptionKeyPropertiesAsync(
                         databaseRid: databaseRidValue,
                         clientEncryptionKeyId: clientEncryptionKeyId,
                         cancellationToken: cancellationToken);
                }

                // update the property level setting.
                foreach (ClientEncryptionIncludedPath propertyToEncrypt in clientEncryptionPolicy.IncludedPaths)
                {
                    EncryptionType encryptionType = GetEncryptionTypeForProperty(propertyToEncrypt);

                    EncryptionSettingForProperty encryptionSettingsForProperty = new EncryptionSettingForProperty(
                        propertyToEncrypt.ClientEncryptionKeyId,
                        encryptionType,
                        encryptionKeyCache,
                        encryptionKeyStoreProvider,
                        databaseRidValue);

                    string propertyName = propertyToEncrypt.Path.Substring(1);

                    encryptionSettings.SetEncryptionSettingForProperty(
                        propertyName,
                        encryptionSettingsForProperty);
                }
            }

            return encryptionSettings;
        }

        private void SetEncryptionSettingForProperty(
            string propertyName,
            EncryptionSettingForProperty encryptionSettingsForProperty)
        {
            this.encryptionSettingsDictByPropertyName[propertyName] = encryptionSettingsForProperty;
        }
    }
}