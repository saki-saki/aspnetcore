﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNet.Cryptography;
using Microsoft.Framework.DependencyInjection;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.DataProtection.KeyManagement
{
    internal sealed class KeyRingProvider : ICacheableKeyRingProvider, IKeyRingProvider
    {
        private CacheableKeyRing _cacheableKeyRing;
        private readonly object _cacheableKeyRingLockObj = new object();
        private readonly ICacheableKeyRingProvider _cacheableKeyRingProvider;
        private readonly IDefaultKeyResolver _defaultKeyResolver;
        private readonly KeyManagementOptions _keyManagementOptions;
        private readonly IKeyManager _keyManager;
        private readonly ILogger _logger;

        public KeyRingProvider(IKeyManager keyManager, KeyManagementOptions keyManagementOptions, IServiceProvider services)
        {
            _keyManagementOptions = new KeyManagementOptions(keyManagementOptions); // clone so new instance is immutable
            _keyManager = keyManager;
            _cacheableKeyRingProvider = services?.GetService<ICacheableKeyRingProvider>() ?? this;
            _logger = services?.GetLogger<KeyRingProvider>();
            _defaultKeyResolver = services?.GetService<IDefaultKeyResolver>()
                ?? new DefaultKeyResolver(_keyManagementOptions.KeyPropagationWindow, _keyManagementOptions.MaxServerClockSkew, services);
        }

        private CacheableKeyRing CreateCacheableKeyRingCore(DateTimeOffset now, bool allowRecursiveCalls = false)
        {
            // Refresh the list of all keys
            var cacheExpirationToken = _keyManager.GetCacheExpirationToken();
            var allKeys = _keyManager.GetAllKeys();

            // Fetch the current default key from the list of all keys
            var defaultKeyPolicy = _defaultKeyResolver.ResolveDefaultKeyPolicy(now, allKeys);
            if (!defaultKeyPolicy.ShouldGenerateNewKey)
            {
                CryptoUtil.Assert(defaultKeyPolicy.DefaultKey != null, "Expected to see a default key.");
                return CreateCacheableKeyRingCoreStep2(now, cacheExpirationToken, defaultKeyPolicy.DefaultKey, allKeys);
            }

            if (_logger.IsVerboseLevelEnabled())
            {
                _logger.LogVerbose("Policy resolution states that a new key should be added to the key ring.");
            }

            // At this point, we know we need to generate a new key.

            // This should only occur if a call to CreateNewKey immediately followed by a call to
            // GetAllKeys returned 'you need to add a key to the key ring'. This should never happen
            // in practice unless there's corruption in the backing store. Regardless, we can't recurse
            // forever, so we have to bail now.
            if (!allowRecursiveCalls)
            {
                if (_logger.IsErrorLevelEnabled())
                {
                    _logger.LogError("Policy resolution states that a new key should be added to the key ring, even after a call to CreateNewKey.");
                }
                throw CryptoUtil.Fail("Policy resolution states that a new key should be added to the key ring, even after a call to CreateNewKey.");
            }

            if (defaultKeyPolicy.DefaultKey == null)
            {
                // We cannot continue if we have no default key and auto-generation of keys is disabled.
                if (!_keyManagementOptions.AutoGenerateKeys)
                {
                    if (_logger.IsErrorLevelEnabled())
                    {
                        _logger.LogError("The key ring does not contain a valid default key, and the key manager is configured with auto-generation of keys disabled.");
                    }
                    throw new InvalidOperationException(Resources.KeyRingProvider_NoDefaultKey_AutoGenerateDisabled);
                }

                // The case where there's no default key is the easiest scenario, since it
                // means that we need to create a new key with immediate activation.
                _keyManager.CreateNewKey(activationDate: now, expirationDate: now + _keyManagementOptions.NewKeyLifetime);
                return CreateCacheableKeyRingCore(now); // recursively call
            }
            else
            {
                // If auto-generation of keys is disabled, we cannot call CreateNewKey.
                if (!_keyManagementOptions.AutoGenerateKeys)
                {
                    if (_logger.IsWarningLevelEnabled())
                    {
                        _logger.LogWarning("Policy resolution states that a new key should be added to the key ring, but automatic generation of keys is disabled.");
                    }
                    return CreateCacheableKeyRingCoreStep2(now, cacheExpirationToken, defaultKeyPolicy.DefaultKey, allKeys);
                }

                // If there is a default key, then the new key we generate should become active upon
                // expiration of the default key. The new key lifetime is measured from the creation
                // date (now), not the activation date.
                _keyManager.CreateNewKey(activationDate: defaultKeyPolicy.DefaultKey.ExpirationDate, expirationDate: now + _keyManagementOptions.NewKeyLifetime);
                return CreateCacheableKeyRingCore(now); // recursively call
            }
        }

        private CacheableKeyRing CreateCacheableKeyRingCoreStep2(DateTimeOffset now, CancellationToken cacheExpirationToken, IKey defaultKey, IEnumerable<IKey> allKeys)
        {
            if (_logger.IsVerboseLevelEnabled())
            {
                _logger.LogVerbose("Using key '{0:D}' as the default key.", defaultKey.KeyId);
            }

            // The cached keyring should expire at the earliest of (default key expiration, next auto-refresh time).
            // Since the refresh period and safety window are not user-settable, we can guarantee that there's at
            // least one auto-refresh between the start of the safety window and the key's expiration date.
            // This gives us an opportunity to update the key ring before expiration, and it prevents multiple
            // servers in a cluster from trying to update the key ring simultaneously.
            return new CacheableKeyRing(
                expirationToken: cacheExpirationToken,
                expirationTime: Min(defaultKey.ExpirationDate, now + GetRefreshPeriodWithJitter(_keyManagementOptions.KeyRingRefreshPeriod)),
                defaultKey: defaultKey,
                allKeys: allKeys);
        }

        public IKeyRing GetCurrentKeyRing()
        {
            return GetCurrentKeyRingCore(DateTime.UtcNow);
        }

        internal IKeyRing GetCurrentKeyRingCore(DateTime utcNow)
        {
            Debug.Assert(utcNow.Kind == DateTimeKind.Utc);

            // Can we return the cached keyring to the caller?
            var existingCacheableKeyRing = Volatile.Read(ref _cacheableKeyRing);
            if (CacheableKeyRing.IsValid(existingCacheableKeyRing, utcNow))
            {
                return existingCacheableKeyRing.KeyRing;
            }

            // The cached keyring hasn't been created or must be refreshed.
            lock (_cacheableKeyRingLockObj)
            {
                // Did somebody update the keyring while we were waiting for the lock?
                existingCacheableKeyRing = Volatile.Read(ref _cacheableKeyRing);
                if (CacheableKeyRing.IsValid(existingCacheableKeyRing, utcNow))
                {
                    return existingCacheableKeyRing.KeyRing;
                }

                if (existingCacheableKeyRing != null && _logger.IsVerboseLevelEnabled())
                {
                    _logger.LogVerbose("Existing cached key ring is expired. Refreshing.");
                }

                // It's up to us to refresh the cached keyring.
                // This call is performed *under lock*.
                var newCacheableKeyRing = _cacheableKeyRingProvider.GetCacheableKeyRing(utcNow);
                Volatile.Write(ref _cacheableKeyRing, newCacheableKeyRing);
                return newCacheableKeyRing.KeyRing;
            }
        }

        private static TimeSpan GetRefreshPeriodWithJitter(TimeSpan refreshPeriod)
        {
            // We'll fudge the refresh period up to -20% so that multiple applications don't try to
            // hit a single repository simultaneously. For instance, if the refresh period is 1 hour,
            // we'll return a value in the vicinity of 48 - 60 minutes. We use the Random class since
            // we don't need a secure PRNG for this.
            return TimeSpan.FromTicks((long)(refreshPeriod.Ticks * (1.0d - (new Random().NextDouble() / 5))));
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b)
        {
            return (a < b) ? a : b;
        }

        CacheableKeyRing ICacheableKeyRingProvider.GetCacheableKeyRing(DateTimeOffset now)
        {
            // the entry point allows one recursive call
            return CreateCacheableKeyRingCore(now, allowRecursiveCalls: true);
        }
    }
}
