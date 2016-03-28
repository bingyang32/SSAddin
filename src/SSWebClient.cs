﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Net;
using System.Diagnostics;
using NCrontab;
using Newtonsoft.Json;


namespace SSAddin {
    #region Deserialization classes
    // rtwebsvr websock format
    class SSTiingoHistPrice {
        public string   date { get; set; }
        public float    open { get; set; }
        public float    close { get; set; }
        public float    high { get; set; }
        public float    low { get; set; }
        public int      volume { get; set; }
        public float    adjOpen { get; set; }
        public float    adjClose { get; set; }
        public float    adjHigh { get; set; }
        public float    adjLow { get; set; }
        public int      adjVolume { get; set; }
    }
    #endregion

    class SSWebClient {
        protected static DataCache      s_Cache = DataCache.Instance( );
        protected static char[]         csvDelimiterChars = { ',' };
        protected static SSWebClient    s_Instance;
        protected static object         s_InstanceLock = new object( );

        protected Queue<String[]>   m_InputQueue;
        protected HashSet<String>   m_InFlight;
        protected Dictionary<String, WSCallback> m_WSCallbacks;

        protected Thread            m_WorkerThread;     // for executing the web query
        protected ManualResetEvent  m_Event;            // control worker thread sleep
        protected String            m_TempDir;
        protected int               m_QuandlCount;        // total number of quandl queries
        protected int               m_TiingoCount;        // total number of quandl queries

        #region Excel thread methods

        protected SSWebClient( )
        {
            m_TempDir = System.IO.Path.GetTempPath( );
            m_InputQueue = new Queue<String[]>( );
            m_InFlight = new HashSet<String>( );
            m_WSCallbacks = new Dictionary<string, WSCallback>( );
            m_Event = new ManualResetEvent( false);
            m_WorkerThread = new Thread( BackgroundWork );
            m_WorkerThread.Start( );
            m_QuandlCount = 0;
            m_TiingoCount = 0;

            // Push out an RTD update for the overall quandl query count. This will mean
            // that trigger parms driven by quandl.all.count don't have #N/A as input for
            // long, which will enable eg ycb_pub_quandl.xls qlPiecewiseYieldCurve to
            // calc almost immediately. JOS 2015-07-29
            UpdateRTD( "quandl", "all", "count", String.Format( "{0}", m_QuandlCount++));
            UpdateRTD( "tiingo", "all", "count", String.Format( "{0}", m_TiingoCount++ ) );
        }

        public static SSWebClient Instance( ) {
            // Unlikley that two threads will attempt to instance this singleton at the
            // same time, but we'll lock just in case.
            lock (s_InstanceLock) {
                if (s_Instance == null) {
                    s_Instance = new SSWebClient( );
                }
                return s_Instance;
            }
        }


        public bool AddRequest( string type, string key, String url, String auth_token="") {
            // Is this job pending or in progress?
            string fkey = String.Format( "{0}.{1}", type, key);
            lock (m_InFlight) {
                if (m_InFlight.Contains( fkey )) {   // Queued or running...
                    Logr.Log( String.Format( "AddRequest: {0} is inflight", fkey ) );
                    return false;                   // so bail
                }
                lock (m_InputQueue) {
                    // Running on the main Excel thread here. Q the work, and
                    // signal the background thread to wake up...
                    Logr.Log( String.Format( "~A AddRequest adding {0} {1} {2} {3}", type, key, url, auth_token ) );
                    String[] req = { type, key, url, auth_token};
                    m_InputQueue.Enqueue( req);
                }
                m_InFlight.Add( fkey);
                m_Event.Set( ); // signal worker thread to wake
            }
            return true;
        }

        #endregion

        #region Pool thread methods

        protected void WSCallbackClosed( string wskey) {
            lock (m_InFlight) {
                // removing the key of the websock allows another incoming request
                // with the same key
                m_InFlight.Remove( wskey );
                m_WSCallbacks.Remove( wskey );
            }
        }

        #endregion

        #region Worker thread methods

        protected String[] GetWork( ) {
            // Put this oneliner in its own method to wrap the locking. We can't
            // hold the lock while we're looping in BackgroundWork( ) as that
            // will prevent the Excel thread adding new requests.
            lock ( m_InputQueue) {
                if (m_InputQueue.Count > 0)
                    return m_InputQueue.Dequeue( );
                return null;
            }
        }

        public void BackgroundWork( ) {
            // We're running on the background thread. Loop until we're told to exit...
            Logr.Log( String.Format( "~A BackgroundWork thread started"));
            // Main loop for worker thread. It will briefly hold the m_InFlight lock when
            // it removes entries, and also the m_InputQueue lock in GetWork( ) as it
            // removes entries. DoQuandlQuery( ) will grab the cache lock when it's
            // adding cache entries. Obviously, no lock should be held permanently! JOS 2015-04-31
            while (true) {
                // Wait for a signal from the other thread to say there's some work.
                m_Event.WaitOne( );
                String[] work = GetWork( );
                while ( work != null) {
                    if (work[0] == "stop") {
                        // exit req from excel thread
                        Logr.Log( String.Format( "~A BackgroundWork thread exiting" ) );
                        return;
                    }
                    string fkey = String.Format( "{0}.{1}", work[0], work[1]);
                    Logr.Log( String.Format( "~A BackgroundWork new request fkey({0})", fkey) );
                    if (work[0] == "quandl") {
                        bool ok = DoQuandlQuery( work[1], work[2]);
                        lock (m_InFlight) {
                            m_InFlight.Remove( fkey );
                        }
                    }
                    else if (work[0] == "tiingo") {
                        bool ok = DoTiingoQuery( work[1], work[2], work[3] );
                        lock (m_InFlight) {
                            m_InFlight.Remove( fkey );
                        }
                    }
                    else if (work[0] == "websock") {
                        WSCallback wscb = new WSCallback( work[1], work[2], this.WSCallbackClosed );
                        lock (m_InFlight) {
                            m_WSCallbacks.Add( fkey, wscb );
                        }
                    }
                    work = GetWork( );
                }
                // We've exhausted the queued work, so reset the event so that we wait in the
                // WaitOne( ) invocation above until another thread signals that there's some
                // more work.
                m_Event.Reset( );
            }
        }

        protected void UpdateRTD( string subcache, string qkey, string subelem, string value ) {
            // The RTD server doesn't necessarily exist. If no cell calls 
            // s2sub( ) it won't be instanced by Excel.
            RTDServer rtd = RTDServer.GetInstance( );
            if ( rtd == null)
                return;
            string stopic = String.Format( "{0}.{1}.{2}", subcache, qkey, subelem );
            rtd.CacheUpdate( stopic, value );
        }


        protected bool DoQuandlQuery( string qkey, string url )
		{
			try	{
                string line = "";
                string lineCount = "0";
                // Set up the web client to HTTP GET
                var client = new WebClient( );
                Stream data = client.OpenRead( url);
                var reader = new StreamReader( data);
                // Local file to dump result
                int pid = Process.GetCurrentProcess( ).Id;
                string csvfname = String.Format( "{0}\\{1}_{2}.csv", m_TempDir, qkey, pid );
                Logr.Log( String.Format( "running quandl qkey({0}) {1} persisted at {2}", qkey, url, csvfname));
                var csvf = new StreamWriter( csvfname );
                UpdateRTD( "quandl", qkey, "status", "starting" );
                // Clear any previous result from the cache so we don't append repeated data
                s_Cache.ClearQuandl( qkey );
                while ( reader.Peek( ) >= 0) {
                    // For each CSV line returned by quandl, dump to localFS, add to in mem cache, and 
                    // send a line count update to any RTD subscriber
                    line = reader.ReadLine( );
                    csvf.WriteLine( line );
                    lineCount = String.Format( "{0}", s_Cache.AddQuandlLine( qkey, line.Split( csvDelimiterChars)));
                    UpdateRTD( "quandl", qkey, "count", lineCount );
                }
                csvf.Close( );
                data.Close();
                reader.Close();
                UpdateRTD( "quandl", qkey, "status", "complete" );
                UpdateRTD( "quandl", "all", "count", String.Format( "{0}", m_QuandlCount++ ) );
                Logr.Log( String.Format( "quandl qkey({0}) complete count({1})", qkey, lineCount));
                return true;
			}
			catch( System.IO.IOException ex) {
                Logr.Log( String.Format( "quandl qkey({0}) {1}", qkey, ex) );
			}
            catch (System.Net.WebException ex) {
                Logr.Log( String.Format( "quandl qkey({0}) {1}", qkey, ex ) );
            }
            return false;
		}

        protected bool DoTiingoQuery( string qkey, string url, string auth_token ) {
            try {
                string line = "";
                string lineCount = "0";
                // Set up the web client to HTTP GET
                var client = new WebClient( );
                client.Headers.Set( "Content-Type", "application/json" );
                client.Headers.Set( "Authorization", auth_token );
                Stream data = client.OpenRead( url );
                var reader = new StreamReader( data );
                // Local file to dump result
                int pid = Process.GetCurrentProcess( ).Id;
                string jsnfname = String.Format( "{0}\\{1}_{2}.jsn", m_TempDir, qkey, pid );
                Logr.Log( String.Format( "running tiingo qkey({0}) {1} persisted at {2}", qkey, url, jsnfname ) );
                var jsnf = new StreamWriter( jsnfname );
                UpdateRTD( "tiingo", qkey, "status", "starting" );
                // Clear any previous result from the cache so we don't append repeated data
                s_Cache.ClearTiingo( qkey );
                StringBuilder sb = new StringBuilder( );
                while (reader.Peek( ) >= 0) {
                    // For each CSV line returned by quandl, dump to localFS, add to in mem cache, and 
                    // send a line count update to any RTD subscriber
                    line = reader.ReadLine( );
                    jsnf.WriteLine( line );
                    sb.AppendLine( line );
                }
                jsnf.Close( );
                data.Close( );
                reader.Close( );
                UpdateRTD( "tiingo", qkey, "status", "complete" );
                UpdateRTD( "tiingo", "all", "count", String.Format( "{0}", m_TiingoCount++ ) );
                Logr.Log( String.Format( "tiingo qkey({0}) complete count({1})", qkey, lineCount ) );
                // TODO: cache the results
                List<SSTiingoHistPrice> updates = JsonConvert.DeserializeObject<List<SSTiingoHistPrice>>( sb.ToString( ));
                s_Cache.UpdateTHPCache( qkey, updates );
                UpdateRTD( "tiingo", qkey, "count", String.Format( "{0}", updates.Count) );
                return true;
            }
            catch (System.IO.IOException ex) {
                Logr.Log( String.Format( "tiingo qkey({0}) {1}", qkey, ex ) );
            }
            catch (System.Net.WebException ex) {
                Logr.Log( String.Format( "tiingo  qkey({0}) {1}", qkey, ex ) );
            }
            return false;
        }
        #endregion
    }
}
