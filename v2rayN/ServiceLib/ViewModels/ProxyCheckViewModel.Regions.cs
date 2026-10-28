using System.ComponentModel;

namespace ServiceLib.ViewModels;

public partial class ProxyCheckViewModel
{
    private IDisposable? _countrySearchSubscription;
    [Reactive] public partial string CountrySearch { get; set; } = "";
    public IReadOnlyList<ProxyCheckCountryChoice> CountryChoices { get; } = ProxyCheckCountries.Codes.Select(c => new ProxyCheckCountryChoice(c)).ToArray();
    public IReadOnlyList<ProxyCheckCountryChoice> FilteredCountries => CountryChoices
        .Where(c => string.Join(' ', c.Code, c.Name, c.EnglishName).Contains(CountrySearch.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .OrderBy(c => c.Name).ToArray();
    public List<string> SelectedCountryCodes => CountryChoices.Where(c => c.IsSelected).Select(c => c.Code).ToList();
    public string SelectedCountriesLabel => SelectedCountryCodes.Count == 0 ? T("未配置地域", "No region expectation")
        : string.Join(" / ", SelectedCountryCodes.Select(ProxyCheckCountries.Name));
    public bool HasCountryMatches => FilteredCountries.Count > 0;
    public string RegionOptionsTitle => T("预期国家/地区 · 独立比较", "Expected countries / regions · Independent comparison");
    public string RegionOptionsHint => T("可搜索、多选，命中任意一个即可。只显示地域匹配结果，不影响 IP 判定、失败或需关注数量。下一轮检查生效；开始检查时保存。", "Search and select multiple regions; any one may match. Region results do not affect IP verdicts, failures or attention counts. Applies and saves when the next check starts.");
    public string RegionSearchHint => T("搜索名称或代码，如 新加坡 / SG", "Search country name or code, e.g. Singapore / SG");
    public string RegionNoMatches => T("没有匹配的国家/地区", "No matching countries or regions");
    public string RegionClearLabel => T("清除选择", "Clear selection");

    private void InitializeCountrySelection()
    {
        foreach (var choice in CountryChoices) choice.PropertyChanged += CountrySelectionChanged;
        _countrySearchSubscription = this.WhenAnyValue(x => x.CountrySearch).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(FilteredCountries));
            this.RaisePropertyChanged(nameof(HasCountryMatches));
        });
    }
    private void CountrySelectionChanged(object? sender, PropertyChangedEventArgs e) => RefreshCountrySelection();
    private void RefreshCountrySelection()
    {
        this.RaisePropertyChanged(nameof(SelectedCountriesLabel));
        this.RaisePropertyChanged(nameof(SelectedCountryCodes));
    }
    public void ClearCountrySelection()
    {
        foreach (var choice in CountryChoices) choice.IsSelected = false;
    }
}
