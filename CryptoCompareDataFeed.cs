﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Quobject.SocketIoClientDotNet.Client;
using WTT.CryptoCompareDataFeed.Properties;
using WTT.IDataFeed;

namespace WTT.CryptoCompareDataFeed
{
    public class CryptoCompareDataFeed : IDataFeed.IDataFeed
    {
        private readonly WebClient _client = new WebClient();

        private readonly ctlLogin _loginForm = new ctlLogin();

        private readonly Queue<HistoryRequest> _requests = new Queue<HistoryRequest>();

        private bool _isWebClientBusy;

        private Socket _socket2;

        private string BaseSymbol { get; set; }
        private string Exchange { get; set; }

        public event EventHandler<MessageEventArgs> OnNewMessage;
        public event EventHandler<DataFeedStatusEventArgs> OnNewStatus;
        public event EventHandler<DataFeedStatusTimeEventArgs> OnNewStatusTime;

        #region Not supported DataFeed methods

        public double GetSymbolOffset(string symbol)
        {
            return 0.0;
        }

        #endregion

        private class HistoryRequest
        {
            public ChartSelection ChartSelection { get; set; }
            public IHistorySubscriber HistorySubscriber { get; set; }
        }

        #region Login/logout

        public string Name => "CryptoCompare";

        public Control GetLoginControl()
        {
            return _loginForm;
        }

        public bool ValidateLoginParams()
        {
            //Check for valid BaseSymbol and Exchange
            //tbd

            BaseSymbol = _loginForm.BaseSymbol;
            Settings.Default.BaseSymbol = BaseSymbol;

            Exchange = _loginForm.Exchange;
            Settings.Default.Exchange = Exchange;
            try
            {
                Settings.Default.Save();
            }
            catch
            {
            }

            return true;
        }

        public bool Login()
        {
            //...Initiate the WebSocket
            var socketAddress = Settings.Default.WebSocket;
            _socket2 = IO.Socket(socketAddress);

            //...Re-connect to smybols in case of event
            _socket2.On(Socket.EVENT_CONNECT, () =>
            {
                FireConnectionStatus("Connected to CryptoCompare streaming socket.");
                //Ensure streaming for symbols
                foreach (var sym in _symbols)
                {
                    var list = "{ subs: ['5~CCCAGG~" + sym + "~" + BaseSymbol + "'] }";
                    var ob = JsonConvert.DeserializeObject<object>(list);
                    _socket2.Emit("SubAdd", ob);
                    //FireConnectionStatus("..adding:" + list);
                }
            });


            //... Monitor incoming messages
            _socket2.On("m", data =>
            {
                var d = (string) data;

                //Decode the socket from CryptoCompare with helper function

                try
                {
                    var unpacked = CurrentUnpack(d);


                    if (unpacked != null)
                    {
                        //DEBUG
                        //var result = string.Join(", ", unpacked.Select(m => m.Key + ":" + m.Value).ToArray());
                        //FireConnectionStatus("U: " + result);

                        //Check if we have received a price update message
                        if (unpacked.TryGetValue("PRICE", out var currentPrice))
                        {
                            var priceUpate = new QuoteData
                            {
                                Symbol = unpacked["FROMSYMBOL"],
                                Price = double.Parse(currentPrice, CultureInfo.InvariantCulture),
                                TradeTime = DateTime.UtcNow
                            };

                            //Update charts
                            BrodcastQuote(priceUpate);

                            //FireConnectionStatus(priceUpate.Symbol+": " + currentPrice + " Time: "+ priceUpate.TradeTime);
                        }
                    }
                }
                catch
                {
                    //could not decode data from streaming socket
                    //should not happen, but just in case...
                    //FireConnectionStatus("Error unpacking quote.");
                }
            });

            return true;
        }

        public bool Logout()
        {
            if (_socket2 != null)
            {
                _socket2.Disconnect();
                _socket2.Close();
            }

            FireConnectionStatus("Disconnected from " + Name);
            return true;
        }

        #endregion

        #region Realtime-feed

        private readonly List<IDataSubscriber> _subscribers = new List<IDataSubscriber>();

        public void InitDataSubscriber(IDataSubscriber subscriber)
        {
            lock (_subscribers)
            {
                if (!_subscribers.Contains(subscriber))
                    _subscribers.Add(subscriber);
            }
        }

        public void RemoveDataSubscriber(IDataSubscriber subscriber)
        {
            lock (_subscribers)
            {
                if (_subscribers.Contains(subscriber))
                {
                    _subscribers.Remove(subscriber);
                    subscriber.UnSubscribeAll();
                }
            }
        }

        private readonly List<string> _symbols = new List<string>();

        public void Subscribe(string symbol)
        {
            //failsafe...
            if (_socket2 == null) return;

            symbol = symbol.ToUpper();

            if (_symbols.Contains(symbol)) return;

            lock (_symbols)
            {
                _symbols.Add(symbol);
            }

            //Format: {SubscriptionId}~{ExchangeName}~{FromSymbol}~{ToSymbol}'
            var subId = "{ subs: ['5~CCCAGG~" + symbol + "~" + BaseSymbol + "'] }";

            FireConnectionStatus("Listening to " + symbol + " / " + BaseSymbol);

            var eObj = JsonConvert.DeserializeObject<object>(subId);

            _socket2.Connect();
            _socket2.Emit("SubAdd", eObj);
            //_socket2.Emit("SubAdd", JObject.FromObject(eObj));
        }

        public void UnSubscribe(string symbol)
        {
            //failsafe...
            if (_socket2 == null) return;

            if (!_symbols.Contains(symbol))
                return;

            lock (_subscribers)
            {
                if (_subscribers.Any(s => s.IsSymbolWatching(symbol)))
                    return;
            }

            lock (_symbols)
            {
                _symbols.Remove(symbol);
            }

            var subId = "{ subs: ['5~CCCAGG~" + symbol + "~" + BaseSymbol + "'] }";

            FireConnectionStatus("De-Listening to " + symbol + " / " + BaseSymbol);

            //'{SubscriptionId}~{ExchangeName}~{FromSymbol}~{ToSymbol}'
            var eObj = JsonConvert.DeserializeObject<object>(subId);

            _socket2.Connect();
            _socket2.Emit("SubRemove", eObj);
        }

        private void BrodcastQuote(QuoteData quote)
        {
            lock (_subscribers)
            {
                foreach (var subscriber in _subscribers) subscriber.OnPriceUpdate(quote);
            }
        }

        #endregion

        #region History

        public void GetHistory(ChartSelection selection, IHistorySubscriber subscriber)
        {
            var request = new HistoryRequest {ChartSelection = selection, HistorySubscriber = subscriber};
            request.ChartSelection.Symbol = request.ChartSelection.Symbol.ToUpper();

            if (!ValidateRequest(request))
            {
                SendNoHistory(request);
                return;
            }

            ThreadPool.QueueUserWorkItem(state => ProcessRequest(request));
        }

        private bool ValidateRequest(HistoryRequest request)
        {
            return request != null &&
                   request.ChartSelection != null &&
                   !string.IsNullOrEmpty(request.ChartSelection.Symbol) &&
                   (request.ChartSelection.Periodicity == EPeriodicity.Daily ||
                    request.ChartSelection.Periodicity == EPeriodicity.Hourly ||
                    request.ChartSelection.Periodicity == EPeriodicity.Minutely
                   ) &&
                   request.HistorySubscriber != null &&
                   request.ChartSelection.ChartType == EChartType.TimeBased;
        }

        private void ProcessRequest(HistoryRequest request)
        {
            _requests.Enqueue(request);
            ProcessNextRequest();
        }

        private void ProcessNextRequest()
        {
            if (_isWebClientBusy || _requests.Count == 0)
                return;

            _isWebClientBusy = true;

            var _currentRequest = _requests.Dequeue();

            var isFail = false;
            var response = string.Empty;


            try
            {
                //DEBUG: FireConnectionStatus(FormRequestString(_currentRequest.ChartSelection));
                response = _client.DownloadString(FormRequestString(_currentRequest.ChartSelection));
            }
            catch (WebException exception)
            {
                try
                {
                    if (exception.Response is HttpWebResponse httpWebResponse)
                    {
                        var resp = new StreamReader(httpWebResponse.GetResponseStream()).ReadToEnd();
                        dynamic obj = JsonConvert.DeserializeObject(resp);
                        var messageFromServer = obj.error_message;
                        MessageBox.Show("Server Exception: " + messageFromServer);
                    }
                }
                catch
                {
                }

                isFail = true;
            }

            if (isFail || string.IsNullOrEmpty(response))
                SendNoHistory(_currentRequest);
            else
                ProcessResponseAndSend(_currentRequest, response);

            _isWebClientBusy = false;

            ProcessNextRequest();
        }

        private string FormRequestString(ChartSelection selection)
        {
            //Specs,
            //https,//www.cryptocompare.com/api/#-api-data-histoday-

            //Set correct endpoint
            var ep = "";
            switch (selection.Periodicity)
            {
                case EPeriodicity.Hourly:
                    ep = @"histohour";
                    break;
                case EPeriodicity.Minutely:
                    ep = @"histominute";
                    break;
                case EPeriodicity.Daily:
                    ep = @"histoday";
                    break;

                default:
                    MessageBox.Show("CryptoCompare supports only days, hours and minutes");
                    return "";
            }

            var requestUrl = Settings.Default.APIUrl +
                             "data/" + ep + "?fsym=" + selection.Symbol + "&tsym=" + BaseSymbol + "&limit=" +
                             selection.Bars + "&aggregate=" + (int) selection.Interval + "&e=" +
                             Exchange; //"&toTs=" + unix;

            FireConnectionStatus("Get: " + requestUrl);
            return requestUrl;
        }

        private void SendNoHistory(HistoryRequest request)
        {
            ThreadPool.QueueUserWorkItem(
                state => request.HistorySubscriber.OnHistoryIncome(request.ChartSelection.Symbol, new List<BarData>()));
        }

        private void ProcessResponseAndSend(HistoryRequest request, string message)
        {
            //failsafe...
            if (string.IsNullOrEmpty(message))
            {
                SendNoHistory(request);
                return;
            }


            var datasets = new List<dynamic>();

            //get the data from the API call
            var master_cryptodataset =
                JsonConvert.DeserializeObject<dynamic>(message);

            //check for data
            if (((ICollection) master_cryptodataset.Data).Count < 4)
            {
                MessageBox.Show("Error: not enough data");
                SendNoHistory(request);
                return;
            }

            //add to API return array
            datasets.Add(master_cryptodataset.Data);


            //for minute requests, try to catch more data from API for a full range of  6 days history
            if (request.ChartSelection.Periodicity == EPeriodicity.Minutely)
            {
                //get the earliest point of data available
                var timeFrom = (long) master_cryptodataset.TimeFrom;
                var dtimeFrom = DateTimeOffset.FromUnixTimeSeconds(timeFrom);
                var dtTimeFrom = dtimeFrom.DateTime;

                var requestString = FormRequestString(request.ChartSelection);

                while (dtTimeFrom > DateTime.UtcNow.AddDays(-6))
                {
                    var isFail = false;
                    var response = string.Empty;

                    try
                    {
                        Debug:
                        FireConnectionStatus(requestString + "&toTs=" + timeFrom);
                        response = _client.DownloadString(requestString + "&toTs=" + timeFrom);
                    }
                    catch
                    {
                        isFail = true;
                    }

                    if (isFail || string.IsNullOrEmpty(response))
                        break;
                    try
                    {
                        var cryptodataset = JsonConvert.DeserializeObject<dynamic>(response);

                        if (((ICollection) cryptodataset.Data).Count > 4)
                        {
                            datasets.Add(cryptodataset.Data);

                            timeFrom = cryptodataset.TimeFrom;
                            dtimeFrom = DateTimeOffset.FromUnixTimeSeconds((long) cryptodataset.TimeFrom);
                            dtTimeFrom = dtimeFrom.DateTime;
                        }
                        else
                        {
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        MessageBox.Show("Could not get crypto dataset: " + e.Message);
                        break;
                    }
                }
            }

            //... convert API return values into bars
            var retBars = new List<BarData>();
            foreach (var set in datasets)
            {
                IEnumerable<BarData> _retBars = FromOHLCVBars(set);

                foreach (var newbar in _retBars)
                {
                    //... skip overlapping bars based on different requests send
                    if (retBars.Any(b => b.TradeDate == newbar.TradeDate))
                        continue;

                    retBars.Add(newbar);
                }
            }

            //...order
            retBars = retBars.OrderBy(data => data.TradeDate).ToList();

            //... if request`s bars amount was <= 0 then send all bars
            if (request.ChartSelection.Bars > 0)
                if (retBars.Count > request.ChartSelection.Bars)
                    retBars = retBars.Skip(retBars.Count - request.ChartSelection.Bars).ToList();

            ThreadPool.QueueUserWorkItem(
                state => request.HistorySubscriber.OnHistoryIncome(request.ChartSelection.Symbol, retBars));
        }

        private IEnumerable<BarData> FromOHLCVBars(dynamic records)
        {
            var count = ((ICollection) records).Count;

            var retBars = new List<BarData>(count);

            foreach (var observation in records)
            {
                var barData = new BarData();

                try
                {
                    var dto = DateTimeOffset.FromUnixTimeSeconds((long) observation.time);
                    var dt = dto.DateTime;

                    barData.TradeDate = dt;

                    barData.Open = (double) observation.open;
                    barData.High = (double) observation.high;
                    barData.Low = (double) observation.low;
                    barData.Close = (double) observation.close;
                    //No Volume, or add barData.Volume =...
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Exception in data parsing:" + ex.Message + "--" + observation.close);
                    continue;
                }

                if (barData.TradeDate == default(DateTime))
                    continue;

                if (barData.Close != 0) retBars.Add(barData); // Dont add empty field with no close...!
            }

            return retBars;
        }

        #endregion

        #region Helper Functions

        private void FireConnectionStatus(string message)
        {
            OnNewMessage?.Invoke(this, new MessageEventArgs {Message = message, Icon = OutputIcon.Warning});
        }

        //Helper function to decode incoming messages from CryptoCompare API WebSocket
        private readonly Dictionary<string, int> _currentFields = new Dictionary<string, int>
        {
            {
                "TYPE", 0x0 // hex for binary 0, it is a special case of fields that are always there
            },
            {
                "MARKET", 0x0 // hex for binary 0, it is a special case of fields that are always there
            },
            {
                "FROMSYMBOL", 0x0 // hex for binary 0, it is a special case of fields that are always there
            },
            {
                "TOSYMBOL", 0x0 // hex for binary 0, it is a special case of fields that are always there
            },
            {
                "FLAGS", 0x0 // hex for binary 0, it is a special case of fields that are always there
            },
            {
                "PRICE", 0x1 // hex for binary 1
            },
            {
                "BID", 0x2 // hex for binary 10
            },
            {
                "OFFER", 0x4 // hex for binary 100
            },
            {
                "LASTUPDATE", 0x8 // hex for binary 1000
            },
            {
                "AVG", 0x10 // hex for binary 10000
            },
            {
                "LASTVOLUME", 0x20 // hex for binary 100000
            },
            {
                "LASTVOLUMETO", 0x40 // hex for binary 1000000
            },
            {
                "LASTTRADEID", 0x80 // hex for binary 10000000
            },
            {
                "VOLUMEHOUR", 0x100 // hex for binary 100000000
            },
            {
                "VOLUMEHOURTO", 0x200 // hex for binary 1000000000
            },
            {
                "VOLUME24HOUR", 0x400 // hex for binary 10000000000
            },
            {
                "VOLUME24HOURTO", 0x800 // hex for binary 100000000000
            },
            {
                "OPENHOUR", 0x1000 // hex for binary 1000000000000
            },
            {
                "HIGHHOUR", 0x2000 // hex for binary 10000000000000
            },
            {
                "LOWHOUR", 0x4000 // hex for binary 100000000000000
            },
            {
                "OPEN24HOUR", 0x8000 // hex for binary 1000000000000000
            },
            {
                "HIGH24HOUR", 0x10000 // hex for binary 10000000000000000
            },
            {
                "LOW24HOUR", 0x20000 // hex for binary 100000000000000000
            },
            {
                "LASTMARKET",
                0x40000 // hex for binary 1000000000000000000, this is a special case and will only appear on CCCAGG messages
            }
        };

        //Decode CryptoCompare message string
        //helper function converted from JS example
        private Dictionary<string, string> CurrentUnpack(string value)
        {
            {
                var valuesArray = value.Split('~');
                var valuesArrayLenght = valuesArray.Length;

                if (valuesArrayLenght <= 1) return null;

                var mask = valuesArray[valuesArrayLenght - 1];
                var maskInt = Convert.ToInt32(mask, 16);

                var unpackedCurrent = new Dictionary<string, string>();

                var currentField = 0;
                foreach (var property in _currentFields.Keys)
                {
                    var hasBit = (maskInt & _currentFields[property]) != 0;

                    if (_currentFields[property] == 0)
                    {
                        try
                        {
                            unpackedCurrent[property] = valuesArray[currentField];
                        }
                        catch
                        {
                        }

                        currentField++;
                    }
                    else if (hasBit)
                    {
                        //i know this is a hack, for cccagg, future code please don't hate me:(, i did this to avoid
                        //subscribing to trades as well in order to show the last market
                        if (property == "LASTMARKET")
                            try
                            {
                                unpackedCurrent[property] = valuesArray[currentField];
                            }
                            catch
                            {
                            }
                        else
                            try
                            {
                                unpackedCurrent[property] = valuesArray[currentField];
                            }
                            catch
                            {
                            }

                        currentField++;
                    }
                }

                return unpackedCurrent;
            }
            
        }

        #endregion
    }
}