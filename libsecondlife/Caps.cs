/*
 * Copyright (c) 2007, Second Life Reverse Engineering Team
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without 
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the Second Life Reverse Engineering Team nor the names 
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" 
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE 
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE 
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE 
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR 
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF 
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN 
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) 
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE 
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.IO;

namespace libsecondlife
{
    /// <summary>
    /// Capabilities is the name of the bi-directional HTTP REST protocol that
    /// Second Life uses to communicate transactions such as teleporting or
    /// asset transfers
    /// </summary>
    public class Caps
    {
        /// <summary>
        /// Triggered when an event is received via the EventQueueGet capability
        /// </summary>
        /// <param name="message">Event name</param>
        /// <param name="body">Decoded event data</param>
        /// <param name="caps">The CAPS system that made the call</param>
        public delegate void EventQueueCallback(string message, Hashtable body, Caps caps);
        /// <summary>
        /// Triggered when an HTTP call in the queue is executed and a response
        /// is received
        /// </summary>
        /// <param name="body">Decoded response</param>
        public delegate void HTTPResponseCallback(Hashtable body);

        /// <summary>Reference to the SecondLife client this system is connected to</summary>
        public SecondLife Client;
        /// <summary>Reference to the simulator this system is connected to</summary>
        public Simulator Simulator;

        /// <summary></summary>
        public string SeedCapsURI { get { return Seedcaps; } }

        internal string Seedcaps;
        internal StringDictionary Capabilities = new StringDictionary();

        private bool Dead = false;
        private Thread CapsThread;
        private string EventQueueCap = String.Empty;
        private HttpWebRequest CapsRequest = null;
        private HttpWebRequest EventQueueRequest = null;

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="client"></param>
        /// <param name="simulator"></param>
        /// <param name="seedcaps"></param>
        internal Caps(SecondLife client, Simulator simulator, string seedcaps)
        {
            Client = client;
            Simulator = simulator;
            Seedcaps = seedcaps;

            CapsThread = new Thread(new ThreadStart(Run));
            CapsThread.Start();
        }

        /// <summary>
        /// Add an outgoing message to the HTTP queue, used for making CAPS
        /// calls
        /// </summary>
        /// <param name="url">CAPS URL to make the POST to</param>
        /// <param name="message">LLSD message to post to the URL</param>
        /// <param name="callback">Callback to fire when the request completes
        /// and a response is available</param>
        public void Post(string url, object message, HTTPResponseCallback callback)
        {
            //FIXME:
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="immediate"></param>
        public void Disconnect(bool immediate)
        {
            Client.DebugLog("Disconnecting CAPS for " + Simulator.ToString());

            Dead = true;

            if (immediate && EventQueueRequest != null)
                EventQueueRequest.Abort();
        }

        private void Run()
        {
            ArrayList req = new ArrayList();
            req.Add("MapLayer");
            req.Add("MapLayerGod");
            req.Add("NewAgentInventory");
            req.Add("NewFileAgentInventory");
            req.Add("EventQueueGet");
            req.Add("UpdateGestureAgentInventory");
            req.Add("UpdateNotecardAgentInventory");
            req.Add("UpdateScriptAgentInventory");
            req.Add("UpdateGestureTaskInventory");
            req.Add("UpdateNotecardTaskInventory");
            req.Add("UpdateScriptTaskInventory");
            req.Add("SendPostcard");
            req.Add("ViewerStartAuction");
            req.Add("ParcelGodReserveForNewbie");
            req.Add("SendUserReport");
            req.Add("SendUserReportWithScreenshot");
            req.Add("RequestTextureDownload");

            byte[] data = LLSD.LLSDSerialize(req);

        MakeRequest:

            try
            {
                CapsRequest = (HttpWebRequest)HttpWebRequest.Create(Seedcaps);
                CapsRequest.KeepAlive = false;
                CapsRequest.Timeout = Client.Settings.CAPS_TIMEOUT;
                CapsRequest.Method = "POST";
                CapsRequest.ContentLength = data.Length;

                CapsRequest.BeginGetRequestStream(new AsyncCallback(CapsRequestCallback), data);

                Client.DebugLog("Requesting initial CAPS request stream");
            }
            catch (WebException e)
            {
                // Thank you .NET creators for giving us such a piss-poor way of checking for HTTP errors
                if (e.Message.Contains("404"))
                {
                    // This capability no longer exists, disable it
                    Disconnect(true);
                    return;
                }
                else if (Dead)
                {
                    // Someone else shut down CAPS while we were trying to connect
                    return;
                }
                else
                {
                    Client.Log("CAPS initialization error for " + Simulator.ToString() + ": " + e.Message +
                        ", retrying", Helpers.LogLevel.Warning);
                    // FIXME: No
                    goto MakeRequest;
                }
            }
        }

        private void CapsRequestCallback(IAsyncResult result)
        {
            byte[] buffer = null;

            try
            {
                Client.DebugLog("Writing to initial CAPS request stream");

                byte[] data = (byte[])result.AsyncState;
                Stream reqStream = CapsRequest.EndGetRequestStream(result);

                reqStream.Write(data, 0, data.Length);
                reqStream.Close();

                Client.DebugLog("Requesting initial CAPS response stream");

                WebResponse response = CapsRequest.GetResponse();
                BinaryReader reader = new BinaryReader(response.GetResponseStream());
                buffer = reader.ReadBytes((int)response.ContentLength);
                response.Close();
            }
            catch (WebException e)
            {
                Client.Log("CAPS error initializing the connection, retrying. " + e.Message,
                    Helpers.LogLevel.Warning);
                Run();
                return;
            }

            if (buffer != null)
            {
                Hashtable resp = (Hashtable)LLSD.LLSDDeserialize(buffer);

                foreach (string cap in resp.Keys)
                {
                    Client.DebugLog(String.Format("Got cap {0}: {1}", cap, (string)resp[cap]));
                    Capabilities[cap] = (string)resp[cap];
                }

                if (Capabilities.ContainsKey("EventQueueGet"))
                {
                    EventQueueCap = Capabilities["EventQueueGet"];
                    Client.DebugLog("Running event queue for " + Simulator.ToString());
                    EventQueueHandler(null);
                }
            }
        }

        private void EventQueueHandler(IAsyncResult result)
        {
            byte[] buffer = null;
            int ack = 0;

            // Special handler for the first request
            if (result == null) goto MakeRequest;

            try
            {
                HttpWebResponse response = (HttpWebResponse)EventQueueRequest.EndGetResponse(result);
                BinaryReader reader = new BinaryReader(response.GetResponseStream());
                buffer = reader.ReadBytes((int)response.ContentLength);
                response.Close();
            }
            catch (WebException e)
            {
                if (e.Message.IndexOf("404") > 0)
                {
                    Client.DebugLog("Got a 404 from the CAPS system, disabling");
                    // This capability no longer exists, disable it
                    Disconnect(true);
                }
                else if (e.Message.IndexOf("502") < 0)
                {
                    Client.DebugLog("EventQueue response exception for " + Simulator.ToString() + ": " + e.Message);
                }
            }

            if (buffer != null)
            {
                lock (Client.Network.EventQueueCallbacks)
                {
                    Hashtable resp = (Hashtable)LLSD.LLSDDeserialize(buffer);
                    ArrayList events = (ArrayList)resp["events"];
                    ack = (int)resp["id"];

                    foreach (Hashtable evt in events)
                    {
                        string msg = (string)evt["message"];
                        Hashtable body = (Hashtable)evt["body"];

                        Client.DebugLog("Event " + msg + ":" + Environment.NewLine + LLSD.LLSDDump(body, 0));

                        for (int i = 0; i < Client.Network.EventQueueCallbacks.Count; i++)
                        {
                            try { Client.Network.EventQueueCallbacks[i](msg, body, this); }
                            catch (Exception e) { Client.Log(e.ToString(), Helpers.LogLevel.Error); }
                        }
                    }
                }
            }

        MakeRequest:

            // Make a new request
            Hashtable req = new Hashtable();
            if (ack != 0) req["ack"] = ack;
            else req["ack"] = null;
            req["done"] = Dead;

            byte[] data = LLSD.LLSDSerialize(req);

            try
            {
                EventQueueRequest = (HttpWebRequest)HttpWebRequest.Create(EventQueueCap);
                EventQueueRequest.KeepAlive = false;
                EventQueueRequest.Method = "POST";
                EventQueueRequest.ContentLength = data.Length;

                Stream reqStream = EventQueueRequest.GetRequestStream();
                reqStream.Write(data, 0, data.Length);
                reqStream.Close();

                if (!Dead)
                    EventQueueRequest.BeginGetResponse(new AsyncCallback(EventQueueHandler), EventQueueRequest);
                else
                    EventQueueRequest.BeginGetResponse(null, EventQueueRequest);
            }
            catch (WebException e)
            {
                Client.DebugLog("EventQueue request exception for " + Simulator.ToString() + ": " + e.Message);
                // If the CAPS system is shutting down don't bother trying too hard
                if (!Dead) goto MakeRequest;
            }
        }
    }
}
