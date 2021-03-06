﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Duplicati.Server.Serialization;
using Duplicati.Server.Serialization.Interface;

namespace Duplicati.GUI.TrayIcon
{
    public class HttpServerConnection : IDisposable
    {
        private const string CONTROL_SCRIPT = "control.cgi";
        private const string LOGIN_SCRIPT = "login.cgi";
        private const string STATUS_WINDOW = "index.html";
        private const string EDIT_WINDOW = "edit-window.html";
        private const string AUTH_COOKIE = "session-auth";
        
        private Uri m_controlUri;
        private string m_baseUri;
        private string m_password;
        private bool m_saltedpassword;
        private string m_authtoken;
        private static readonly System.Text.Encoding ENCODING = System.Text.Encoding.GetEncoding("utf-8");

        public delegate void StatusUpdateDelegate(IServerStatus status);
        public event StatusUpdateDelegate OnStatusUpdated;

        public long m_lastNotificationId = -1;
        public DateTime m_firstNotificationTime;
        public delegate void NewNotificationDelegate(INotification notification);
        public event NewNotificationDelegate OnNotification;

        private volatile IServerStatus m_status;

        private volatile bool m_shutdown = false;
        private volatile System.Threading.Thread m_requestThread;
        private volatile System.Threading.Thread m_pollThread;
        private System.Threading.AutoResetEvent m_waitLock;

        private readonly Dictionary<string, string> m_updateRequest;

        public IServerStatus Status { get { return m_status; } }

        private object m_lock = new object();
        private Queue<Dictionary<string, string>> m_workQueue = new Queue<Dictionary<string,string>>();

        public HttpServerConnection(Uri server, string password, bool saltedpassword)
        {
            m_baseUri = server.ToString();
            if (!m_baseUri.EndsWith("/"))
                m_baseUri += "/";
            
            m_firstNotificationTime = DateTime.Now;

            m_controlUri = new Uri(m_baseUri + CONTROL_SCRIPT);
            m_password = password;
            m_saltedpassword = saltedpassword;

            m_updateRequest = new Dictionary<string, string>();
            m_updateRequest["action"] = "get-current-state";
            m_updateRequest["longpoll"] = "false";
            m_updateRequest["lasteventid"] = "0";

            UpdateStatus();

            //We do the first request without long poll,
            // and all the rest with longpoll
            m_updateRequest["longpoll"] = "true";
            m_updateRequest["duration"] = "5m";
            
            m_waitLock = new System.Threading.AutoResetEvent(false);
            m_requestThread = new System.Threading.Thread(ThreadRunner);
            m_pollThread = new System.Threading.Thread(LongPollRunner);

            m_requestThread.Name = "TrayIcon Request Thread";
            m_pollThread.Name = "TrayIcon Longpoll Thread";

            m_requestThread.Start();
            m_pollThread.Start();
        }

        private void UpdateStatus()
        {
            m_status = PerformRequest<IServerStatus>(m_updateRequest);
            m_updateRequest["lasteventid"] = m_status.LastEventID.ToString();

            if (OnStatusUpdated != null)
                OnStatusUpdated(m_status);

            if (m_lastNotificationId != m_status.LastNotificationUpdateID)
            {
                m_lastNotificationId = m_status.LastNotificationUpdateID;
                UpdateNotifications();
            }
        }

        private void UpdateNotifications()
        {
            var req = new Dictionary<string, string>();
            req["action"] = "get-notifications";

            var notifications = PerformRequest<INotification[]>(req);
            if (notifications != null)
            {
                foreach(var n in notifications.Where(x => x.Timestamp > m_firstNotificationTime))
                    if (OnNotification != null)
                        OnNotification(n);

                if (notifications.Any())
                    m_firstNotificationTime = notifications.Select(x => x.Timestamp).Max();
            }
        }

        private void LongPollRunner()
        {
            while (!m_shutdown)
            {
                try
                {
                    UpdateStatus();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Request error: " + ex.Message);
                    Console.WriteLine("Request error: " + ex.Message);
                }
            }
        }

        private void ThreadRunner()
        {
            while (!m_shutdown)
            {
                try
                {
                    Dictionary<string, string> req;
                    bool any = false;
                    do
                    {
                        req = null;

                        lock (m_lock)
                            if (m_workQueue.Count > 0)
                                req = m_workQueue.Dequeue();

                        if (m_shutdown)
                            break;

                        if (req != null)
                        {
                            any = true;
                            PerformRequest<string>(req);
                        }
                    
                    } while (req != null);
                    
                    if (!(any || m_shutdown))
                        m_waitLock.WaitOne(TimeSpan.FromMinutes(1), true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine("Request error: " + ex.Message);
                    Console.WriteLine("Request error: " + ex.Message);
                }
            }
        }

        public void Close()
        {
            m_shutdown = true;
            m_waitLock.Set();
            m_pollThread.Abort();
            m_pollThread.Join(TimeSpan.FromSeconds(10));
            if (!m_requestThread.Join(TimeSpan.FromSeconds(10)))
            {
                m_requestThread.Abort();
                m_requestThread.Join(TimeSpan.FromSeconds(10));
            }
        }

        private static string EncodeQueryString(Dictionary<string, string> dict)
        {
            return string.Join("&", Array.ConvertAll(dict.Keys.ToArray(), key => string.Format("{0}={1}", Uri.EscapeUriString(key), Uri.EscapeUriString(dict[key]))));
        }

        private class SaltAndNonce
        {
            public string Salt = null;
            public string Nonce = null;
        }

        private SaltAndNonce GetSaltAndNonce()
        {
            var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(m_baseUri + LOGIN_SCRIPT + "?get-nonce=1");
            req.Method = "GET";
            req.UserAgent = "Duplicati TrayIcon Monitor, v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();

            Duplicati.Library.Utility.AsyncHttpRequest areq = new Library.Utility.AsyncHttpRequest(req);
            using(var r = (System.Net.HttpWebResponse)areq.GetResponse())
            using(var s = areq.GetResponseStream())
            using (var sr = new System.IO.StreamReader(s, ENCODING, true))
                return Serializer.Deserialize<SaltAndNonce>(sr);
        }

        private string PerformLogin(string password, string nonce)
        {
            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(m_baseUri + LOGIN_SCRIPT + "?password=" + Duplicati.Library.Utility.Uri.UrlEncode(password));
            req.Method = "GET";
            req.UserAgent = "Duplicati TrayIcon Monitor, v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            if (req.CookieContainer == null)
                req.CookieContainer = new System.Net.CookieContainer();
            req.CookieContainer.Add(new System.Net.Cookie("session-nonce", nonce, "/", req.RequestUri.Host));

            //Wrap it all in async stuff
            Duplicati.Library.Utility.AsyncHttpRequest areq = new Library.Utility.AsyncHttpRequest(req);
            using(var r = (System.Net.HttpWebResponse)areq.GetResponse())
                if (r.StatusCode == System.Net.HttpStatusCode.OK)
                    return (r.Cookies[AUTH_COOKIE] ?? r.Cookies[Library.Utility.Uri.UrlEncode(AUTH_COOKIE)]).Value;

            return null;
        }

        private string GetAuthToken()
        {
            var salt_nonce = GetSaltAndNonce();
            var sha256 = System.Security.Cryptography.SHA256.Create();
            var password = m_password;

            if (!m_saltedpassword)
            {
                var str = System.Text.Encoding.UTF8.GetBytes(m_password);
                var buf = Convert.FromBase64String(salt_nonce.Salt);
                sha256.TransformBlock(str, 0, str.Length, str, 0);
                sha256.TransformFinalBlock(buf, 0, buf.Length);
                password = Convert.ToBase64String(sha256.Hash);
                sha256.Initialize();
            }

            var nonce = Convert.FromBase64String(salt_nonce.Nonce);
            sha256.TransformBlock(nonce, 0, nonce.Length, nonce, 0);
            var pwdbuf = Convert.FromBase64String(password);
            sha256.TransformFinalBlock(pwdbuf, 0, pwdbuf.Length);
            var pwd = Convert.ToBase64String(sha256.Hash);

            return PerformLogin(pwd, salt_nonce.Nonce);
        }


        private T PerformRequest<T>(Dictionary<string, string> queryparams)
        {
            try
            {
                return PerformRequestInternal<T>(queryparams);
            }
            catch (System.Net.WebException wex)
            {
                if (
                    wex.Status == System.Net.WebExceptionStatus.ProtocolError && 
                    ((System.Net.HttpWebResponse)wex.Response).StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                    !string.IsNullOrWhiteSpace(m_password)
                )
                {
                    m_authtoken = GetAuthToken();
                    return PerformRequestInternal<T>(queryparams);
                }
                else
                    throw;
            }
        }

        private T PerformRequestInternal<T>(Dictionary<string, string> queryparams)
        {
            queryparams["format"] = "json";

            string query = EncodeQueryString(queryparams);
            byte[] data = ENCODING.GetBytes(query);

            System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(m_controlUri);
            req.Method = "POST";
            req.ContentLength = data.Length;
            req.ContentType = "application/x-www-form-urlencoded ; charset=" + ENCODING.BodyName;
            req.Headers.Add("Accept-Charset", ENCODING.BodyName);
            req.UserAgent = "Duplicati TrayIcon Monitor, v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            if (m_authtoken != null)
            {   
                if (req.CookieContainer == null)
                    req.CookieContainer = new System.Net.CookieContainer();
                req.CookieContainer.Add(new System.Net.Cookie(AUTH_COOKIE, m_authtoken, "/", req.RequestUri.Host));
            }
            
            //Wrap it all in async stuff
            Duplicati.Library.Utility.AsyncHttpRequest areq = new Library.Utility.AsyncHttpRequest(req);

            using (System.IO.Stream s = areq.GetRequestStream())
                s.Write(data, 0, data.Length);

            //Assign the timeout, and add a little processing time as well
            if (queryparams["action"] == "get-current-state" && queryparams.ContainsKey("duration"))
                areq.Timeout = (int)(Duplicati.Library.Utility.Timeparser.ParseTimeSpan(queryparams["duration"]) + TimeSpan.FromSeconds(5)).TotalMilliseconds;

            using(System.Net.HttpWebResponse r = (System.Net.HttpWebResponse)areq.GetResponse())
            using (System.IO.Stream s = areq.GetResponseStream())
                if (typeof(T) == typeof(string))
                {
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
                    {
                        s.CopyTo(ms);
                        return (T)(object)ENCODING.GetString(ms.ToArray());
                    }
                }
                else
                {
                    using (var sr = new System.IO.StreamReader(s, ENCODING, true))
                        return Serializer.Deserialize<T>(sr);
                }

        }

        private void ExecuteAndNotify(Dictionary<string, string> req)
        {
            lock (m_lock)
            {
                m_workQueue.Enqueue(req);
                m_waitLock.Set();
            }
        }

        public void Pause(string duration = null)
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "send-command");
            req.Add("command", "pause");
            if (!string.IsNullOrWhiteSpace(duration))
                req.Add("duration", duration);

            ExecuteAndNotify(req);
        }

        public void Resume()
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "send-command");
            req.Add("command", "resume");
            ExecuteAndNotify(req);
        }

        public void StopBackup()
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "send-command");
            req.Add("command", "stop");
            ExecuteAndNotify(req);
        }

        public void AbortBackup()
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "send-command");
            req.Add("command", "abort");
            ExecuteAndNotify(req);
        }

        public void RunBackup(long id, bool forcefull = false)
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "send-command");
            req.Add("command", "run-backup");
            req.Add("id", id.ToString());
            if (forcefull)
                req.Add("full", "true");
            ExecuteAndNotify(req);
        }
  
        public void DismissNotification(long id)
        {
            Dictionary<string, string> req = new Dictionary<string, string>();
            req.Add("action", "dismiss-notification");
            req.Add("id", id.ToString());
            ExecuteAndNotify(req);
        }

        public void Dispose()
        {
            Close();
        }
        
        public string StatusWindowURL
        {
            get 
            { 
                try
                {
                    if (m_authtoken != null)
                        return m_baseUri + STATUS_WINDOW + "?auth-token=" + GetAuthToken();
                }
                catch
                {
                }
                
                return m_baseUri + STATUS_WINDOW; 
            }
        }

        public string EditWindowURL
        {
            get { return m_baseUri + EDIT_WINDOW; }
        }

    }
}
