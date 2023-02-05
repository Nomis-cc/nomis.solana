﻿// ------------------------------------------------------------------------------------------------------
// <copyright file="SoulboundTokenSettings.cs" company="Nomis">
// Copyright (c) Nomis, 2023. All rights reserved.
// The Application under the MIT license. See LICENSE file in the solution root for full license information.
// </copyright>
// ------------------------------------------------------------------------------------------------------

using Nomis.SoulboundTokenService.Interfaces.Enums;
using Nomis.SoulboundTokenService.Models;
using Nomis.Utils.Contracts.Common;

namespace Nomis.SoulboundTokenService.Settings
{
    /// <summary>
    /// Soulbound token settings.
    /// </summary>
    public class SoulboundTokenSettings :
        ISettings
    {
        /// <summary>
        /// EVM Signer-wallet private key.
        /// </summary>
        public string? EvmWalletPrivateKey { get; set; }

        /// <summary>
        /// Token data by score type.
        /// </summary>
        public Dictionary<ScoreType, SoulboundTokenData> TokenData { get; set; } = new();
    }
}