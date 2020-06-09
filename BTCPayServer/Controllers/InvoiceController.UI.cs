﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Filters;
using BTCPayServer.Models;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Changelly;
using BTCPayServer.Payments.CoinSwitch;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Security;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Invoices.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using NBitcoin;
using NBitpayClient;
using NBXplorer;
using Newtonsoft.Json.Linq;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Controllers
{
    public partial class InvoiceController
    {
        [HttpGet]
        [Route("invoices/{invoiceId}")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        public async Task<IActionResult> Invoice(string invoiceId)
        {
            var invoice = (await _InvoiceRepository.GetInvoices(new InvoiceQuery()
            {
                InvoiceId = new[] {invoiceId},
                UserId = GetUserId(),
                IncludeAddresses = true,
                IncludeEvents = true,
                IncludeArchived = true,
            })).FirstOrDefault();
            if (invoice == null)
                return NotFound();

            var prodInfo = invoice.ProductInformation;
            var store = await _StoreRepository.FindStore(invoice.StoreId);
            var model = new InvoiceDetailsModel()
            {
                StoreName = store.StoreName,
                StoreLink = Url.Action(nameof(StoresController.UpdateStore), "Stores", new { storeId = store.Id }),
                Id = invoice.Id,
                State = invoice.GetInvoiceState().ToString(),
                TransactionSpeed = invoice.SpeedPolicy == SpeedPolicy.HighSpeed ? "high" :
                                   invoice.SpeedPolicy == SpeedPolicy.MediumSpeed ? "medium" :
                                   invoice.SpeedPolicy == SpeedPolicy.LowMediumSpeed ? "low-medium" :
                                   "low",
                RefundEmail = invoice.RefundMail,
                CreatedDate = invoice.InvoiceTime,
                ExpirationDate = invoice.ExpirationTime,
                MonitoringDate = invoice.MonitoringExpiration,
                OrderId = invoice.OrderId,
                BuyerInformation = invoice.BuyerInformation,
                Fiat = _CurrencyNameTable.DisplayFormatCurrency(prodInfo.Price, prodInfo.Currency),
                TaxIncluded = _CurrencyNameTable.DisplayFormatCurrency(prodInfo.TaxIncluded, prodInfo.Currency),
                NotificationUrl = invoice.NotificationURL?.AbsoluteUri,
                RedirectUrl = invoice.RedirectURL?.AbsoluteUri,
                ProductInformation = invoice.ProductInformation,
                StatusException = invoice.ExceptionStatus,
                Events = invoice.Events,
                PosData = PosDataParser.ParsePosData(invoice.PosData),
                Archived = invoice.Archived
            };

            model.Addresses = invoice.HistoricalAddresses.Select(h =>
                new InvoiceDetailsModel.AddressModel
                {
                    Destination = h.GetAddress(),
                    PaymentMethod = h.GetPaymentMethodId().ToPrettyString(),
                    Current = !h.UnAssigned.HasValue
                }).ToArray();

            var details = InvoicePopulatePayments(invoice);
            model.CryptoPayments = details.CryptoPayments;
            model.Payments = details.Payments;

            return View(model);
        }
        private InvoiceDetailsModel InvoicePopulatePayments(InvoiceEntity invoice)
        {
            var model = new InvoiceDetailsModel();
            model.Archived = invoice.Archived;
            model.Payments = invoice.GetPayments();
            foreach (var data in invoice.GetPaymentMethods())
            {
                var accounting = data.Calculate();
                var paymentMethodId = data.GetId();
                var cryptoPayment = new InvoiceDetailsModel.CryptoPayment();
                
                cryptoPayment.PaymentMethodId = paymentMethodId;
                cryptoPayment.PaymentMethod = paymentMethodId.ToPrettyString();
                cryptoPayment.Due = _CurrencyNameTable.DisplayFormatCurrency(accounting.Due.ToDecimal(MoneyUnit.BTC), paymentMethodId.CryptoCode);
                cryptoPayment.Paid = _CurrencyNameTable.DisplayFormatCurrency(accounting.CryptoPaid.ToDecimal(MoneyUnit.BTC), paymentMethodId.CryptoCode);
                cryptoPayment.Overpaid = _CurrencyNameTable.DisplayFormatCurrency(accounting.OverpaidHelper.ToDecimal(MoneyUnit.BTC), paymentMethodId.CryptoCode);
                var paymentMethodDetails = data.GetPaymentMethodDetails();
                cryptoPayment.Address = paymentMethodDetails.GetPaymentDestination();
                cryptoPayment.Rate = _CurrencyNameTable.ExchangeRate(data);
                model.CryptoPayments.Add(cryptoPayment);
            }
            return model;
        }

        [HttpPost("invoices/{invoiceId}/archive")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> ToggleArchive(string invoiceId)
        {
            var invoice = (await _InvoiceRepository.GetInvoices(new InvoiceQuery()
            {
                InvoiceId = new[] {invoiceId}, UserId = GetUserId(), IncludeAddresses = true, IncludeEvents = true, IncludeArchived = true,
            })).FirstOrDefault();
            if (invoice == null)
                return NotFound();
            await _InvoiceRepository.ToggleInvoiceArchival(invoiceId, !invoice.Archived);
            TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = invoice.Archived ? "The invoice has been unarchived and will appear in the invoice list by default again." : "The invoice has been archived and will no longer appear in the invoice list by default."
            });
            return RedirectToAction(nameof(invoice), new {invoiceId});
        }

        [HttpGet]
        [Route("i/{invoiceId}/status/ws")]
        [Route("i/{invoiceId}/{paymentMethodId}/status/ws")]
        [Route("invoice/{invoiceId}/status/ws")]
        [Route("invoice/{invoiceId}/{paymentMethodId}/status")]
        [Route("invoice/status/ws")]
        public async Task<IActionResult> GetStatusWebSocket(string invoiceId)
        {
            if (!HttpContext.WebSockets.IsWebSocketRequest)
                return NotFound();
            var invoice = await _InvoiceRepository.GetInvoice(invoiceId);
            if (invoice == null || invoice.Status == InvoiceStatus.Complete || invoice.Status == InvoiceStatus.Invalid || invoice.Status == InvoiceStatus.Expired)
                return NotFound();
            var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            CompositeDisposable leases = new CompositeDisposable();
            try
            {
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceDataChangedEvent>(async o => await NotifySocket(webSocket, o.InvoiceId, invoiceId)));
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceNewAddressEvent>(async o => await NotifySocket(webSocket, o.InvoiceId, invoiceId)));
                leases.Add(_EventAggregator.Subscribe<Events.InvoiceEvent>(async o => await NotifySocket(webSocket, o.Invoice.Id, invoiceId)));
                while (true)
                {
                    var message = await webSocket.ReceiveAsync(DummyBuffer, default(CancellationToken));
                    if (message.MessageType == WebSocketMessageType.Close)
                        break;
                }
            }
            catch(WebSocketException) { }
            finally
            {
                leases.Dispose();
                await webSocket.CloseSocket();
            }
            return new EmptyResult();
        }

        ArraySegment<Byte> DummyBuffer = new ArraySegment<Byte>(new Byte[1]);
        private async Task NotifySocket(WebSocket webSocket, string invoiceId, string expectedId)
        {
            if (invoiceId != expectedId || webSocket.State != WebSocketState.Open)
                return;
            using CancellationTokenSource cts = new CancellationTokenSource();
            cts.CancelAfter(5000);
            try
            {
                await webSocket.SendAsync(DummyBuffer, WebSocketMessageType.Binary, true, cts.Token);
            }
            catch { try { webSocket.Dispose(); } catch { } }
        }

        [HttpPost]
        [Route("i/{invoiceId}/UpdateCustomer")]
        [Route("invoice/UpdateCustomer")]
        public async Task<IActionResult> UpdateCustomer(string invoiceId, [FromBody]UpdateCustomerModel data)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }
            await _InvoiceRepository.UpdateInvoice(invoiceId, data).ConfigureAwait(false);
            return Ok("{}");
        }

        [HttpGet]
        [Route("invoices")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> ListInvoices(string searchTerm = null, int skip = 0, int count = 50, int timezoneOffset = 0)
        {
            var fs = new SearchString(searchTerm);
            var storeIds = fs.GetFilterArray("storeid") != null ? fs.GetFilterArray("storeid") : new List<string>().ToArray();

            var model = new InvoicesModel
            {
                SearchTerm = searchTerm,
                Skip = skip,
                Count = count,
                StoreIds = storeIds,
                TimezoneOffset = timezoneOffset
            };
            InvoiceQuery invoiceQuery = GetInvoiceQuery(searchTerm, timezoneOffset);
            var counting = _InvoiceRepository.GetInvoicesTotal(invoiceQuery);
            invoiceQuery.Count = count;
            invoiceQuery.Skip = skip;
            var list = await _InvoiceRepository.GetInvoices(invoiceQuery);

            foreach (var invoice in list)
            {
                var state = invoice.GetInvoiceState();
                model.Invoices.Add(new InvoiceModel()
                {
                    Status = invoice.Status,
                    StatusString = state.ToString(),
                    ShowCheckout = invoice.Status == InvoiceStatus.New,
                    Date = invoice.InvoiceTime,
                    InvoiceId = invoice.Id,
                    OrderId = invoice.OrderId ?? string.Empty,
                    RedirectUrl = invoice.RedirectURL?.AbsoluteUri ?? string.Empty,
                    AmountCurrency = _CurrencyNameTable.DisplayFormatCurrency(invoice.ProductInformation.Price, invoice.ProductInformation.Currency),
                    CanMarkInvalid = state.CanMarkInvalid(),
                    CanMarkComplete = state.CanMarkComplete(),
                    Details = InvoicePopulatePayments(invoice),
                });
            }
            model.Total = await counting;
            return View(model);
        }

        private InvoiceQuery GetInvoiceQuery(string searchTerm = null, int timezoneOffset = 0)
        {
            var fs = new SearchString(searchTerm);
            var invoiceQuery = new InvoiceQuery()
            {
                TextSearch = fs.TextSearch,
                UserId = GetUserId(),
                Unusual = fs.GetFilterBool("unusual"),
                IncludeArchived = fs.GetFilterBool("includearchived") ?? false,
                Status = fs.GetFilterArray("status"),
                ExceptionStatus = fs.GetFilterArray("exceptionstatus"),
                StoreId = fs.GetFilterArray("storeid"),
                ItemCode = fs.GetFilterArray("itemcode"),
                OrderId = fs.GetFilterArray("orderid"),
                StartDate = fs.GetFilterDate("startdate", timezoneOffset),
                EndDate = fs.GetFilterDate("enddate", timezoneOffset)
            };
            return invoiceQuery;
        }

        [HttpGet]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> Export(string format, string searchTerm = null, int timezoneOffset = 0)
        {
            var model = new InvoiceExport(_CurrencyNameTable);

            InvoiceQuery invoiceQuery = GetInvoiceQuery(searchTerm, timezoneOffset);
            invoiceQuery.Skip = 0;
            invoiceQuery.Count = int.MaxValue;
            var invoices = await _InvoiceRepository.GetInvoices(invoiceQuery);
            var res = model.Process(invoices, format);

            var cd = new ContentDisposition
            {
                FileName = $"btcpay-export-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.{format}",
                Inline = true
            };
            Response.Headers.Add("Content-Disposition", cd.ToString());
            Response.Headers.Add("X-Content-Type-Options", "nosniff");
            return Content(res, "application/" + format);
        }


        private SelectList GetPaymentMethodsSelectList()
        {
            return new SelectList(_paymentMethodHandlerDictionary.Distinct().SelectMany(handler =>
                    handler.GetSupportedPaymentMethods()
                        .Select(id => new SelectListItem(id.ToPrettyString(), id.ToString()))),
                nameof(SelectListItem.Value),
                nameof(SelectListItem.Text));
        }

        [HttpGet]
        [Route("invoices/create")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> CreateInvoice()
        {
            var stores = new SelectList(await _StoreRepository.GetStoresByUserId(GetUserId()), nameof(StoreData.Id), nameof(StoreData.StoreName), null);
            if (!stores.Any())
            {
                TempData[WellKnownTempData.ErrorMessage] = "You need to create at least one store before creating a transaction";
                return RedirectToAction(nameof(UserStoresController.ListStores), "UserStores");
            }

            return View(new CreateInvoiceModel() { Stores = stores, AvailablePaymentMethods = GetPaymentMethodsSelectList() });
        }

        [HttpPost]
        [Route("invoices/create")]
        [Authorize(Policy = Policies.CanCreateInvoice, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> CreateInvoice(CreateInvoiceModel model, CancellationToken cancellationToken)
        {
            var stores = await _StoreRepository.GetStoresByUserId(GetUserId());
            model.Stores = new SelectList(stores, nameof(StoreData.Id), nameof(StoreData.StoreName), model.StoreId);
            model.AvailablePaymentMethods = GetPaymentMethodsSelectList();
            var store = HttpContext.GetStoreData();
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            if (!store.GetSupportedPaymentMethods(_NetworkProvider).Any())
            {
                ModelState.AddModelError(nameof(model.StoreId), "You need to configure the derivation scheme in order to create an invoice");
                return View(model);
            }

            try
            {
                var result = await CreateInvoiceCore(new CreateInvoiceRequest()
                {
                    Price = model.Amount.Value,
                    Currency = model.Currency,
                    PosData = model.PosData,
                    OrderId = model.OrderId,
                    //RedirectURL = redirect + "redirect",
                    NotificationURL = model.NotificationUrl,
                    ItemDesc = model.ItemDesc,
                    FullNotifications = true,
                    BuyerEmail = model.BuyerEmail,
                    SupportedTransactionCurrencies = model.SupportedTransactionCurrencies?.ToDictionary(s => s, s => new InvoiceSupportedTransactionCurrency()
                    {
                        Enabled = true
                    })
                }, store, HttpContext.Request.GetAbsoluteRoot(), cancellationToken: cancellationToken);

                TempData[WellKnownTempData.SuccessMessage] = $"Invoice {result.Data.Id} just created!";
                return RedirectToAction(nameof(ListInvoices));
            }
            catch (BitpayHttpException ex)
            {
                ModelState.TryAddModelError(nameof(model.Currency), $"Error: {ex.Message}");
                return View(model);
            }
        }

        [HttpPost]
        [Route("invoices/{invoiceId}/changestate/{newState}")]
        [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [BitpayAPIConstraint(false)]
        public async Task<IActionResult> ChangeInvoiceState(string invoiceId, string newState)
        {
            var invoice = (await _InvoiceRepository.GetInvoices(new InvoiceQuery()
            {
                InvoiceId = new[] {invoiceId},
                UserId = GetUserId()
            })).FirstOrDefault();
            var model = new InvoiceStateChangeModel();
            if (invoice == null)
            {
                model.NotFound = true;
                return NotFound(model);
            }
            if (newState == "invalid")
            {
                await _InvoiceRepository.UpdatePaidInvoiceToInvalid(invoiceId);
                _EventAggregator.Publish(new InvoiceEvent(invoice, 1008, InvoiceEvent.MarkedInvalid));
                model.StatusString = new InvoiceState("invalid", "marked").ToString();
            }
            else if (newState == "complete")
            {
                await _InvoiceRepository.UpdatePaidInvoiceToComplete(invoiceId);
                _EventAggregator.Publish(new InvoiceEvent(invoice, 2008, InvoiceEvent.MarkedCompleted));
                model.StatusString = new InvoiceState("complete", "marked").ToString();
            }

            return Json(model);
        }

        public class InvoiceStateChangeModel
        {
            public bool NotFound { get; set; }
            public string StatusString { get; set; }
        }

        private string GetUserId()
        {
            return _UserManager.GetUserId(User);
        }

        public class PosDataParser
        {
            public static Dictionary<string, object> ParsePosData(string posData)
            {
                var result = new Dictionary<string, object>();
                if (string.IsNullOrEmpty(posData))
                {
                    return result;
                }

                try
                {
                    var jObject = JObject.Parse(posData);
                    foreach (var item in jObject)
                    {

                        switch (item.Value.Type)
                        {
                            case JTokenType.Array:
                                var items = item.Value.AsEnumerable().ToList();
                                for (var i = 0; i < items.Count; i++)
                                {
                                    result.Add($"{item.Key}[{i}]", ParsePosData(items[i].ToString()));
                                }
                                break;
                            case JTokenType.Object:
                                result.Add(item.Key, ParsePosData(item.Value.ToString()));
                                break;
                            default:
                                result.Add(item.Key, item.Value.ToString());
                                break;
                        }

                    }
                }
                catch
                {
                    result.Add(string.Empty, posData);
                }
                return result;
            }
        }

    }
}
