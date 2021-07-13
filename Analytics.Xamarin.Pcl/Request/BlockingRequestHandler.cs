using System;
using System.Text;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;

using Segment.Model;
using Segment.Exception;
using Segment.Delegates;

namespace Segment.Request
{
	class WebProxy : System.Net.IWebProxy
	{
		private string _proxy;

		public WebProxy(string proxy)
		{
			_proxy = proxy;
			GetProxy(new Uri(proxy)); // ** What does this do?
		}

		public System.Net.ICredentials Credentials
		{
			get; set;
		}

		public Uri GetProxy(Uri destination)
		{
			if (!String.IsNullOrWhiteSpace(destination.ToString()))
				return destination;
			else
				return new Uri("");
		}

		public bool IsBypassed(Uri host)
		{
			if (!String.IsNullOrWhiteSpace(host.ToString()))
				return true;
			else
				return false;
		}
	}
	internal class BlockingRequestHandler : IRequestHandler
	{
		/// <summary>
		/// Occurs when an action fails.
		/// </summary>
		public event FailedActionHandler Failed;

		/// <summary>
		/// Occurs when an action succeeds.
		/// </summary>
		public event SucceededActionHandler Succeeded;

		/// <summary>
		/// Segment client
		/// </summary>
		private readonly Client _client;

		/// <summary>
		/// Http client
		/// </summary>
		private readonly HttpClient _httpClient;

		

		internal BlockingRequestHandler(Client client, TimeSpan timeout)
		{
			this._client = client;

			if (!string.IsNullOrEmpty(_client.Config.Proxy))
			{
				var handler = new HttpClientHandler
				{
					Proxy = new WebProxy(_client.Config.Proxy),
					UseProxy = true
				};
				this._httpClient = new HttpClient(handler);
			}
			else
				this._httpClient = new HttpClient();

			this._httpClient.Timeout = timeout;
			// do not use the expect 100-continue behavior
			this._httpClient.DefaultRequestHeaders.ExpectContinue = false;
		}

		public void SendBatch(Batch batch)
		{
			Dict props = new Dict {
				{ "batch id", batch.MessageId },
				{ "batch size", batch.batch.Count }
			};

			try
			{
				// set the current request time
				batch.SentAt = DateTime.Now.ToString("o");
				string json = JsonConvert.SerializeObject(batch);
				props["json size"] = json.Length;

				Uri uri = new Uri(_client.Config.Host + "/v1/import");

				HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uri);

				// basic auth: https://segment.io/docs/tracking-api/reference/#authentication
				request.Headers.Add("Authorization", BasicAuthHeader(batch.WriteKey, ""));
				request.Content = new StringContent(json, Encoding.UTF8, "application/json");

				Logger.Info("Sending analytics request to Segment.io ..", props);

				var start = DateTime.Now;

				var response = _httpClient.SendAsync(request).Result;

				var duration = DateTime.Now - start;
				props["success"] = response.IsSuccessStatusCode;
				props["duration (ms)"] = duration.TotalMilliseconds;

				if (response.IsSuccessStatusCode)
				{
					Succeed(batch);
					Logger.Info("Request successful", props);
				}
				else
				{
					string reason = string.Format("Status Code {0} ", response.StatusCode);
					reason += response.Content.ToString();
					props["reason"] = reason;
					Logger.Error("Request failed", props);
					Fail(batch, new APIException("Unexpected Status Code", reason));
				}
			}
			catch (System.Exception e)
			{
				props["reason"] = e.Message;
				Logger.Error("Request failed", props);
				Fail(batch, e);
			}
		}

		private void Fail(Batch batch, System.Exception e)
		{
			foreach (BaseAction action in batch.batch)
			{
				if (Failed != null)
				{
					Failed(action, e);
				}
			}
		}


		private void Succeed(Batch batch)
		{
			foreach (BaseAction action in batch.batch)
			{
				if (Succeeded != null)
				{
					Succeeded(action);
				}
			}
		}

		private string BasicAuthHeader(string user, string pass)
		{
			string val = user + ":" + pass;
			return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(val));
		}

		public void Dispose()
		{
			_client.Dispose();
		}
	}
}

