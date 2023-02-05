// ------------------------------------------------------------------------------------------------------
// <copyright file="SolscanService.cs" company="Nomis">
// Copyright (c) Nomis, 2023. All rights reserved.
// The Application under the MIT license. See LICENSE file in the solution root for full license information.
// </copyright>
// ------------------------------------------------------------------------------------------------------

using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomis.Blockchain.Abstractions;
using Nomis.Blockchain.Abstractions.Contracts;
using Nomis.Blockchain.Abstractions.Extensions;
using Nomis.Blockchain.Abstractions.Models;
using Nomis.Blockchain.Abstractions.Requests;
using Nomis.Blockchain.Abstractions.Stats;
using Nomis.Coingecko.Interfaces;
using Nomis.DefiLlama.Interfaces;
using Nomis.DefiLlama.Interfaces.Contracts;
using Nomis.DefiLlama.Interfaces.Extensions;
using Nomis.DefiLlama.Interfaces.Models;
using Nomis.Domain.Scoring.Entities;
using Nomis.MagicEden.Interfaces;
using Nomis.ScoringService.Interfaces;
using Nomis.Solscan.Calculators;
using Nomis.Solscan.Interfaces;
using Nomis.Solscan.Interfaces.Extensions;
using Nomis.Solscan.Interfaces.Models;
using Nomis.Solscan.Settings;
using Nomis.SoulboundTokenService.Interfaces;
using Nomis.Utils.Contracts.Services;
using Nomis.Utils.Exceptions;
using Nomis.Utils.Wrapper;
using Solnet.Wallet;

namespace Nomis.Solscan
{
    /// <inheritdoc cref="ISolanaScoringService"/>
    internal sealed class SolscanService :
        BlockchainDescriptor,
        ISolanaScoringService,
        IHasDefiLlamaChainId,
        ITransientService
    {
        private readonly IMagicEdenClient _magicEdenClient;
        private readonly ICoingeckoService _coingeckoService;
        private readonly IScoringService _scoringService;
        private readonly INonEvmSoulboundTokenService _soulboundTokenService;
        private readonly IDefiLlamaService _defiLlamaService;
        private readonly ILogger<SolscanService> _logger;
        private readonly ISolscanClient _client;

        /// <summary>
        /// Initialize <see cref="SolscanService"/>.
        /// </summary>
        /// <param name="settings"><see cref="SolscanSettings"/>.</param>
        /// <param name="magicEdenClient"><see cref="IMagicEdenClient"/>.</param>
        /// <param name="solscanClient"><see cref="ISolscanClient"/>.</param>
        /// <param name="coingeckoService"><see cref="ICoingeckoService"/>.</param>
        /// <param name="scoringService"><see cref="IScoringService"/>.</param>
        /// <param name="soulboundTokenService"><see cref="INonEvmSoulboundTokenService"/>.</param>
        /// <param name="defiLlamaService"><see cref="IDefiLlamaService"/>.</param>
        /// <param name="logger"><see cref="ILogger{T}"/>.</param>
        public SolscanService(
            IOptions<SolscanSettings> settings,
            IMagicEdenClient magicEdenClient,
            ISolscanClient solscanClient,
            ICoingeckoService coingeckoService,
            IScoringService scoringService,
            INonEvmSoulboundTokenService soulboundTokenService,
            IDefiLlamaService defiLlamaService,
            ILogger<SolscanService> logger)
            : base(settings.Value.BlockchainDescriptor)
        {
            _magicEdenClient = magicEdenClient;
            _coingeckoService = coingeckoService;
            _scoringService = scoringService;
            _soulboundTokenService = soulboundTokenService;
            _defiLlamaService = defiLlamaService;
            _logger = logger;
            _client = solscanClient;
        }

        /// <inheritdoc />
        public string DefiLLamaChainId => "solana";

        /// <inheritdoc/>
        public async Task<Result<TWalletScore>> GetWalletStatsAsync<TWalletStatsRequest, TWalletScore, TWalletStats, TTransactionIntervalData>(
            TWalletStatsRequest request,
            CancellationToken cancellationToken = default)
            where TWalletStatsRequest : WalletStatsRequest
            where TWalletScore : IWalletScore<TWalletStats, TTransactionIntervalData>, new()
            where TWalletStats : class, IWalletCommonStats<TTransactionIntervalData>, new()
            where TTransactionIntervalData : class, ITransactionIntervalData
        {
            try
            {
                var publicKey = new PublicKey(request.Address);
                if (!publicKey.IsValid())
                {
                    throw new InvalidAddressException(request.Address);
                }
            }
            catch (ArgumentException e)
            {
                throw new CustomException(e.Message);
            }

            decimal balance = await _client.GetBalanceAsync(request.Address);
            decimal usdBalance =
                (await _defiLlamaService.GetTokensPriceAsync(new List<string> { "coingecko:solana" }))?.TokensPrices["coingecko:solana"].Price ?? 0 * balance;
            var magicEdenWalletActivities = (await _magicEdenClient.GetWalletActivitiesData(request.Address)).ToList();
            var splTransfers = await _client.GetTransfersDataAsync<SolscanSplTransferList, SolscanSplTransfer>(request.Address);
            var solTransfers = await _client.GetTransfersDataAsync<SolscanSolTransferList, SolscanSolTransfer>(request.Address);
            var accountTokens = (await _client.GetTokensAsync(request.Address)).ToList();

            #region Tokens data

            var tokensData = new List<(string TokenContractId, string TokenContractIdWithBlockchain, decimal? Balance)>();
            if ((request as IWalletTokensBalancesRequest)?.GetHoldTokensBalances == true)
            {
                var tokens = accountTokens
                    .Where(x => !string.IsNullOrWhiteSpace(x.TokenAddress) && !string.IsNullOrWhiteSpace(x.TokenSymbol))
                    .Select(x => new
                    {
                        x.TokenAddress,
                        Amount = x.TokenAmount?.Amount.ToDecimal()
                    });
                foreach (var token in tokens.DistinctBy(y => y.TokenAddress))
                {
                    if (token.Amount > 0)
                    {
                        tokensData.Add((token.TokenAddress!, $"{DefiLLamaChainId}:{token.TokenAddress}", token.Amount));
                    }
                }
            }

            #endregion Tokens data

            #region Tokens balances (DefiLlama and Coingecko)

            var tokenBalances = new List<TokenBalanceData>();
            if ((request as IWalletTokensBalancesRequest)?.GetHoldTokensBalances == true)
            {
                var tokenPrices = await _defiLlamaService.GetTokensPriceAsync(
                    tokensData.Select(t => t.TokenContractIdWithBlockchain).ToList(),
                    (request as IWalletTokensBalancesRequest)?.SearchWidthInHours ?? 6);
                if (tokenPrices != null)
                {
                    tokenBalances.AddRange(tokenPrices.GetTokenBalanceData(tokensData).ToList());
                }

                var otherTokens = tokensData
                    .Where(t => !tokenBalances.Any(x => x.TokenId.Equals(t.TokenContractId, StringComparison.InvariantCultureIgnoreCase)));
                foreach (var token in otherTokens)
                {
                    await Task.Delay(100, cancellationToken);
                    var tokenData = await _coingeckoService.GetTokenDataAsync("solana", token.TokenContractId);
                    if (tokenData != null && tokenData.DetailPlatforms.ContainsKey("solana") && !string.IsNullOrWhiteSpace(tokenData.Id))
                    {
                        int decimals = tokenData.DetailPlatforms["solana"].DecimalPlace;
                        decimal tokenBalance = token.Balance ?? 0;
                        for (int i = 0; i < decimals; i++)
                        {
                            tokenBalance /= 10;
                        }

                        await Task.Delay(100, cancellationToken);
                        decimal tokenUsdBalance =
                            (await _defiLlamaService.GetTokensPriceAsync(new List<string> { $"coingecko:{tokenData.Id}" }))?.TokensPrices[$"coingecko:{tokenData.Id}"].Price * tokenBalance ?? 0;

                        tokenBalances.Add(new TokenBalanceData(
                            new DefiLlamaTokenPriceData
                            {
                                Confidence = 0.9M,
                                Decimals = decimals,
                                Price = tokenUsdBalance / tokenBalance,
                                Timestamp = (ulong)new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(),
                                Symbol = tokenData.Symbol
                            },
                            token.TokenContractId,
                            token.TokenContractIdWithBlockchain,
                            token.Balance));
                    }
                }
            }

            #endregion Tokens balances (DefiLlama and Coingecko)

            var walletStats = new SolanaStatCalculator(
                    request.Address,
                    balance,
                    usdBalance,
                    magicEdenWalletActivities,
                    splTransfers,
                    solTransfers,
                    accountTokens,
                    tokenBalances)
                .GetStats() as TWalletStats;

            double score = walletStats!.GetScore<TWalletStats, TTransactionIntervalData>();
            var scoringData = new ScoringData(request.Address, request.Address, ChainId, score, JsonSerializer.Serialize(walletStats));
            await _scoringService.SaveScoringDataToDatabaseAsync(scoringData, cancellationToken);

            // getting signature
            ushort mintedScore = (ushort)(score * 10000);
            var signatureResult = _soulboundTokenService.GetSoulboundTokenSignature(new()
            {
                Score = mintedScore,
                ScoreType = request.ScoreType,
                To = request.Address,
                Nonce = request.Nonce,
                ChainId = ChainId,
                ContractAddress = SBTContractAddresses?.ContainsKey(request.ScoreType) == true ? SBTContractAddresses?[request.ScoreType] : null,
                Deadline = request.Deadline
            });

            var messages = signatureResult.Messages;
            messages.Add($"Got {ChainName} wallet {request.ScoreType.ToString()} score.");
            return await Result<TWalletScore>.SuccessAsync(
                new()
                {
                    Address = request.Address,
                    Stats = walletStats,
                    Score = score,
                    MintedScore = mintedScore,
                    Signature = signatureResult.Data.Signature
                }, messages);
        }

        /// <inheritdoc/>
        public async Task<Result<List<SolanaWalletScore>>> GetWalletsStatsAsync(
            List<string> addresses,
            ulong nonce = 0,
            ulong deadline = 0,
            CancellationToken cancellationToken = default)
        {
            int counter = 0;

            // use throttler bc of solscan API limitations
            var throttler = new SemaphoreSlim(5);

            var result = new List<SolanaWalletScore>();
            var tasks = addresses
                .Select(async address =>
                {
                    await throttler.WaitAsync(cancellationToken);

                    var task = GetWalletStatsAsync<WalletStatsRequest, SolanaWalletScore, SolanaWalletStats, SolanaTransactionIntervalData>(
                        new()
                        {
                            Address = address,
                            Nonce = nonce,
                            Deadline = deadline
                        }, cancellationToken);
                    _ = task.ContinueWith(
                        async _ =>
                        {
                            Interlocked.Increment(ref counter);
                            _logger.LogWarning("{Counter} - Stats for {Wallet} calculated!", counter, address);

                            await Task.Delay(10, cancellationToken);
                            throttler.Release();
                        }, cancellationToken);

                    try
                    {
                        return await task;
                    }
                    catch (HttpRequestException)
                    {
                        return await task;
                    }
                });
            var statsResults = await Task.WhenAll(tasks);
            result.AddRange(statsResults.Where(r => r.Succeeded).Select(r => r.Data));

            return await Result<List<SolanaWalletScore>>.SuccessAsync(result, "Got solana wallets score.");
        }
    }
}