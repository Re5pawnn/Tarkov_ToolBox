using System.ComponentModel;
using TarkovMapLocator.ModuleContracts;

namespace TarkovMapLocator.Modules.Market;

internal sealed record MarketOfferRow(string Source, string Price);

internal sealed class MarketItemRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    public MarketItemRow(FeatureMarketItem item, bool isExpanded)
    {
        Item = item;
        _isExpanded = isExpanded;
        Offers = item.SellFor
            .OrderByDescending(offer => offer.Price)
            .Select(offer => new MarketOfferRow(
                string.Equals(offer.Source, "fleaMarket", StringComparison.OrdinalIgnoreCase)
                    ? "跳蚤市场"
                    : string.IsNullOrWhiteSpace(offer.VendorName) ? offer.Source : offer.VendorName,
                FormatPrice(offer.Price)))
            .ToArray();
    }

    public FeatureMarketItem Item { get; }
    public string Id => Item.Id;
    public string DisplayName => string.IsNullOrWhiteSpace(Item.NameZh) ? Item.Name : Item.NameZh;
    public string Aliases
    {
        get
        {
            var aliases = string.IsNullOrWhiteSpace(Item.ShortNameZh) ? Item.ShortName : Item.ShortNameZh;
            if (!string.IsNullOrWhiteSpace(Item.Name) && !string.Equals(Item.Name, Item.NameZh, StringComparison.Ordinal))
                aliases += $" · {Item.Name}";
            return aliases;
        }
    }

    public string EffectiveIconLink => Item.GridImageLink is { Length: > 0 } gridImage ? gridImage : Item.IconLink;
    public string FallbackIconLink => Item.IconLink;
    public string PrimaryPrice => FormatPrice(Item.FleaPrice ?? Item.Avg24hPrice);
    public string PriceCaption => Item.FleaPrice is null ? "24 小时均价" : "跳蚤市场";
    public string PriceSummary => $"跳蚤 {FormatPrice(Item.FleaPrice)}   24h 均价 {FormatPrice(Item.Avg24hPrice)}   低 / 高 {FormatPrice(Item.Low24hPrice)} / {FormatPrice(Item.High24hPrice)}   最近 {FormatPrice(Item.LastLowPrice)}";
    public string BestTraderText => Item.BestTrader is { } trader
        ? $"最高商人报价：{trader.VendorName} {FormatPrice(trader.Price)}"
        : "";
    public string TypesText => Item.Types.Count > 0 ? $"类型：{string.Join(" · ", Item.Types.Take(4))}" : "";
    public IReadOnlyList<MarketOfferRow> Offers { get; }
    public string EmptyOffersText => Offers.Count == 0 ? "暂无可用报价" : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string FormatPrice(long? value) => value is { } price and > 0 ? $"{price:N0} ₽" : "—";
}
