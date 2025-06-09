using RetailCorrector;
using RetailCorrector.Plugin;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Validators;

[assembly: Guid("9f838119-7899-4c77-92a1-4d4060d6a3c8")]

namespace Saby
{
    public class Plugin : SourcePlugin
    {
        public override string Name => "Saby";

        [DisplayName(Constants.LoginName)]
        public string Login { get; set; } = "";

        [DisplayName(Constants.PasswdName)]
        public string Password { get; set; } = "";

        [DisplayName(Constants.VatinName)]
        public string Vatin
        {
            get => _vatin;
            set
            {
                if (VatinValidator.Valid(value))
                    _vatin = value;
                else Notify(Constants.VatinError);
            }
        }
        private string _vatin = "";

        [DisplayName(Constants.DeviceName)]
        public string RegId
        {
            get => _regId;
            set
            {
                if (DeviceValidator.Valid(value))
                    _regId = value;
                else Notify(Constants.DeviceError);
            }
        }
        private string _regId = "";

        [DisplayName(Constants.StorageName)]
        public string StorageId
        {
            get => _storage;
            set
            {
                if (DeviceValidator.Valid(value))
                    _storage = value;
                else Notify(Constants.StorageError);
            }
        }
        private string _storage = "";

        [DisplayName(Constants.DateFromName)]
        public DateOnly Start
        {
            get => _start;
            set
            {
                if (value <= _end)
                    _start = value;
                else Notify(Constants.DateFromError);
            }
        }
        private DateOnly _start = DateOnly.FromDateTime(DateTime.Today);

        [DisplayName(Constants.DateToName)]
        public DateOnly End
        {
            get => _end;
            set
            {
                if (value <= _start)
                    _end = value;
                else Notify(Constants.DateToError);
            }
        }
        private DateOnly _end = DateOnly.FromDateTime(DateTime.Today);

        private HttpClient? http = null;
        private string key = "";

        public override Task OnLoad(AssemblyLoadContext ctx)
        {
            http = new HttpClient
            {
                BaseAddress = new Uri(Constants.BaseUri),
                Timeout = TimeSpan.FromSeconds(15),
            };
            return Task.FromResult(true);
        }

        public override Task OnUnload()
        {
            http?.Dispose();
            http = null;
            return Task.CompletedTask;
        }

        public override async Task<IEnumerable<Receipt>> Parse(CancellationToken token)
        {
            key = await GetToken(token).ConfigureAwait(false);
            var list = new List<Receipt>(); 
            for(var date = Start; date<=End; date = date.AddDays(1))
                list.AddRange(await ParseByDay(date, token).ConfigureAwait(false));
            return list;
        }

        private async Task<string> GetToken(CancellationToken token)
        {
            var data = new { app_client_id = 1025293145607151, login = Login, password = Password };
            using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, "/oauth/service/");
            req.Content = new StringContent(JsonSerializer.Serialize(data), MediaTypeHeaderValue.Parse("application/json"));
            using var resp = await http!.SendAsync(req, token).ConfigureAwait(false);
            var content = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return JsonNode.Parse(content)?["sid"]?.GetValue<string>()!;
        }

        private async Task<List<Receipt>> ParseByDay(DateOnly day, CancellationToken token)
        {
            var url = new StringBuilder($"ofd/v1/orgs/{Vatin}/kkts/{RegId}/storages/{StorageId}/docs");
            url.Append($"?dateFrom={day.ToString(Constants.DateOnlyFormat)}T00:00:00");
            url.Append($"&dateTo={day.ToString(Constants.DateOnlyFormat)}T23:59:59"); // startId
            using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, url.ToString());
            req.Headers.Add("Cookie", $"sid={key}");
            using var resp = await http!.SendAsync(req, token).ConfigureAwait(false);
            var content = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var json = JsonNode.Parse(content)!.AsArray();
            var list = new List<Receipt>();
            foreach (var node in json)
            {
                var raw = node!["receipt"];
                if (raw is null) continue;
                var receipt = new Receipt
                {
                    Created = new DateTime(1970,1,1) + TimeSpan.FromSeconds(raw["dateTime"]!.GetValue<long>()),
                    FiscalSign = raw["fiscalSign"]!.GetValue<long>().ToString(),
                    TotalSum = raw["totalSum"]!.GetValue<uint>(),
                    Operation = (Operation)raw["operationType"]!.GetValue<int>(),
                    Items = new Position[raw["items"]!.AsArray().Count],
                    Payment = new Payment
                    {
                        Cash = raw["cashTotalSum"]!.GetValue<uint>(),
                        ECash = raw["ecashTotalSum"]!.GetValue<uint>(),
                        Pre = raw["prepaidSum"]!.GetValue<uint>(),
                        Post = raw["creditSum"]!.GetValue<uint>(),
                        Provision = raw["provisionSum"]!.GetValue<uint>()
                    }
                };
                for(var i = 0; i < receipt.Items.Length; i++)
                {
                    var item = raw["items"]!.AsArray()[i]!;
                    receipt.Items[i] = new Position
                    {
                        Name = item["name"]!.GetValue<string>(),
                        Price = item["price"]!.GetValue<uint>(),
                        Quantity = (uint)Math.Round(item["quantity"]!.GetValue<double>()*1000),
                        TotalSum = item["sum"]!.GetValue<uint>(),
                        TaxRate = (TaxRate)item["nds"]!.GetValue<int>(),
                        PayType = (PaymentType)item["paymentType"]!.GetValue<int>(),
                        PosType = (PositionType)item["productType"]!.GetValue<int>(),
                        MeasureUnit = (MeasureUnit?)item["itemsQuantityMeasure"]?.GetValue<int>() ?? MeasureUnit.None
                    };
                }
                list.Add(receipt);
            }
            return list;
        }
    }
}
