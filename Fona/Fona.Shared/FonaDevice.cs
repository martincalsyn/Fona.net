﻿using System;
using System.Collections;
using System.IO.Ports;
using System.Text;
using System.Threading;

// Shared FonaDevice implementation

namespace Molarity.Hardare.AdafruitFona
{
    /// <summary>
    /// Event handler for the FonaDevice.Ringing event. This event handler type is used to process
    /// an incoming call or SMS text.
    /// </summary>
    /// <param name="sender">The FonaDevice that detected the incoming call or text</param>
    /// <param name="args">Arguments describing the incoming call</param>
    public delegate void RingingEventHandler(object sender, RingingEventArgs args);

    /// <summary>
    /// This class supports interactions with the Adafruit Fona GSM/GPRS breakout board.
    /// </summary>
    public partial class FonaDevice
    {
        private const string AT = "AT";
        private const string OK = "OK";
        private const string EchoOffCommand = "ATE0";
        private const string UnlockCommand = "AT+CPIN=";
        private const string GetCcidCommand = "AT+CCID";
        private const string GetImeiCommand = "AT+GSN";
        private const string ReadRtcCommand = "AT+CCLK?";
        private const string ReadRtcReply = "+CCLK: ";
        private const string SetLocalTimeStampEnable = "AT+CLTS=";
        private const string ReadLocalTimeStampEnable = "AT+CLTS?";
        private const string ReadLocalTimeStampEnableReply = "+CLTS: ";
        private const string WriteNvram = "AT&W";

        private readonly SerialPort _port;
        private readonly object _lockSendExpect = new object();

        /// <summary>
        /// This event is raised when an incoming call is detected. If an RI pin is available, the hardware indication is used.
        /// Otherwise, this class will look for the string "RING" from the Fona device. When not using the hardware pin, you may
        /// receive more than one ring indication for a single call because the board sends the RING string multiple times.
        /// </summary>
        public event RingingEventHandler Ringing;

        /// <summary>
        /// Create a FonaDevice class for use in communicating with the Adafruit Fona GSM/GPRS breakout board.
        /// </summary>
        /// <param name="port">This port should be configured for 8 data bits, one stop bit, no parity at pretty much any speed. The Fona device will autobaud when you call .Reset</param>
        public FonaDevice(SerialPort port)
        {
            _port = port;
            _port.DataReceived += PortOnDataReceived;
            _port.Open();
        }

        /// <summary>
        /// Reset the Fona device. Note that you must call this at least one so that the Fona device can auto-baud and synchronize with the
        /// baud rate you selected on your serial port.
        /// </summary>
        public void Reset()
        {
            DoHardwareReset();

            DiscardBufferedInput();

            // Synchronize auto-baud
            for (int i = 0; i < 3; ++i)
            {
                try
                {
                    SendAndExpect(AT, OK);
                    Thread.Sleep(100);
                }
                catch (FonaException)
                {
                    // Ignore exceptions that occur during synchronization
                }
            }

            try
            {
                SendAndExpect(EchoOffCommand, OK);
                Thread.Sleep(100);
            }
            catch (FonaException)
            {
                // Ignore exceptions that occur during synchronization
            }

            // Throw, if we don't get an expected response here
            SendAndExpect(EchoOffCommand, OK);
        }

        /// <summary>
        /// Pass a four-digit code to unlock the SIM. WARNING: Calling this more than three times with the wrong code may lock your SIM and
        /// render it unusable until you unlock it with a special PUK code from your GSM provider.
        /// </summary>
        /// <param name="code"></param>
        public void UnlockSim(string code)
        {
            string command = UnlockCommand + code;
            SendAndExpect(command, OK);
        }

        /// <summary>
        /// Get the CCID identifier from your SIM. This is a number that uniquely identifies your SIM.
        /// </summary>
        /// <returns>SIM CCID</returns>
        public string GetSimCcid()
        {
            var response = SendCommandAndReadReply(GetCcidCommand);
            Expect(OK);
            return response;
        }

        /// <summary>
        /// Retrieve the IMEI for the Fona device.  This is a number that uniquely identifies your Fona breakout board.
        /// </summary>
        /// <returns>IMEI code from the Fona device</returns>
        public string GetImei()
        {
            var response = SendCommandAndReadReply(GetImeiCommand);
            Expect(OK);
            return response;
        }

        /// <summary>
        /// Read the current time from the real-time clock on the Fona
        /// </summary>
        /// <returns>The current date and time</returns>
        public DateTime GetCurrentTime()
        {
            var tokens = SendCommandAndParseReply(ReadRtcCommand, ReadRtcReply, ',', true);

            // We should have two tokens - a date part and a time part
            if (tokens.Length!=2)
                throw new FonaCommandException(ReadRtcReply, ReadRtcReply, tokens[0]);

            var dateTokens = tokens[0].Split('/');
            if (dateTokens.Length!=3)
                throw new FonaException("Bad date format");
            var year = 2000 + int.Parse(dateTokens[0]);
            var month = int.Parse(dateTokens[1]);
            var day = int.Parse(dateTokens[2]);

            var timeTokens = tokens[1].Split(':');
            if (timeTokens.Length!=3)
                throw new FonaException("Bad time format");
            var temp = timeTokens[2].Split('+');
            timeTokens[2] = temp[0];
            int tz = 0;
            if (temp.Length==2)
                tz = int.Parse(temp[1]);
            var hour = int.Parse(timeTokens[0]);
            var minute = int.Parse(timeTokens[1]);
            var second = int.Parse(timeTokens[2]);

            var result = new DateTime(year,month,day,hour,minute,second);
            //result.AddHours(tz);
            return new DateTime(result.Ticks, DateTimeKind.Local);
        }

        /// <summary>
        /// Check or set the current state of the local timestamp setting.
        /// When this is set, the Fona will return the network-provided local time.
        /// When not set, the time that is returned is based on a manual setting and not
        /// the network provider's cell-tower time. Note that after changing the setting,
        /// you must reset the device by calling Reset (with a hardware reset pin defined)
        /// or power the Fona device off and then on again.
        /// </summary>
        public bool RtcEnabled
        {
            get
            {
                var reply = SendCommandAndReadReply(ReadLocalTimeStampEnable, ReadLocalTimeStampEnableReply, false);
                return (int.Parse(reply) != 0);
            }
            set
            {
                if (value == this.RtcEnabled)
                    return;
                SendAndExpect(SetLocalTimeStampEnable + (value ? "1" : "0"), OK);
                SendAndExpect(WriteNvram, OK);
            }

        }

        #region Sending Commands

        private void SendAndExpect(string send, string expect)
        {
            SendAndExpect(send, expect, -1);
        }

        private void SendAndExpect(string send, string expect, int timeout)
        {
            lock (_lockSendExpect)
            {
                DiscardBufferedInput();
                WriteLine(send);
                Expect(new[] { send }, expect, timeout);
            }
        }

        private string SendCommandAndReadReply(string command, string replyPrefix, bool replyIsQuoted)
        {
            return SendCommandAndReadReply(command, replyPrefix, replyIsQuoted, -1);
        }

        private string SendCommandAndReadReply(string command, string replyPrefix, bool replyIsQuoted, int timeout)
        {
            var reply = SendCommandAndReadReply(command, -1);
            if (reply.IndexOf(replyPrefix) != 0)
            {
                throw new FonaCommandException(command, replyPrefix, reply);
            }
            reply = reply.Substring(replyPrefix.Length);
            if (replyIsQuoted)
            {
                reply = reply.Trim();
                var idxQuote = reply.IndexOf('"');
                if (idxQuote == 0)
                    reply = reply.Substring(1);
                idxQuote = reply.LastIndexOf('"');
                if (idxQuote == reply.Length - 1)
                    reply = reply.Substring(0, reply.Length - 1);
            }
            return reply;
        }

        private string[] SendCommandAndParseReply(string command, string replyPrefix, char delimiter, bool replyIsQuoted)
        {
            return SendCommandAndParseReply(command, replyPrefix, delimiter, replyIsQuoted, -1);
        }

        private string[] SendCommandAndParseReply(string command, string replyPrefix, char delimiter, bool replyIsQuoted, int timeout)
        {
            var reply = SendCommandAndReadReply(command, replyPrefix, replyIsQuoted, timeout);
            return reply.Split(delimiter);
        }

        private string SendCommandAndReadReply(string command)
        {
            return SendCommandAndReadReply(command, -1);
        }

        private string SendCommandAndReadReply(string command, int timeout)
        {
            string response;
            lock (_lockSendExpect)
            {
                DiscardBufferedInput();
                WriteLine(command);
                do
                {
                    response = GetReplyWithTimeout(timeout);
                } while (response == null || response == "");
            }
            return response;
        }

        #endregion

        #region Parse responses

        private void Expect(string expect)
        {
            Expect(null, expect, -1);
        }

        private void Expect(string expect, int timeout)
        {
            Expect(null, expect, timeout);
        }

        private void Expect(string[] accept, string expect)
        {
            Expect(accept, expect, -1);
        }

        private void Expect(string[] accept, string expect, int timeout)
        {
            if (accept == null)
                accept = new[] {""};

            bool acceptableInputFound;
            string response;
            do
            {
                acceptableInputFound = false;
                response = GetReplyWithTimeout(timeout);

                foreach (var s in accept)
                {
#if MF_FRAMEWORK
                    if (response=="" || string.Equals(response.ToLower(), s.ToLower()))
#else
                    if (response=="" || string.Equals(response, s, StringComparison.OrdinalIgnoreCase))
#endif
                    {
                        acceptableInputFound = true;
                        break;
                    }
                }
            } while (acceptableInputFound);
#if MF_FRAMEWORK
            if (!string.Equals(response.ToLower(), expect.ToLower()))
#else
            if (!string.Equals(response, expect, StringComparison.OrdinalIgnoreCase))
#endif
            {
                throw new FonaExpectException(expect, response);
            }
        }

        private string GetReplyWithTimeout(int timeout)
        {
            string response = null;
            bool haveNewData;
            do
            {
                lock (_responseQueueLock)
                {
                    if (_responseQueue.Count > 0)
                    {
                        response = (string)_responseQueue[0];
                        _responseQueue.RemoveAt(0);
                    }
                    else
                    {
                        _responseReceived.Reset();
                    }
                }

                // If nothing was waiting in the queue, then wait for new data to arrive
                haveNewData = false;
                if (response == null)
                    haveNewData = _responseReceived.WaitOne(timeout, false);

            } while (response==null && haveNewData);

            // We have received no data, and the WaitOne timed out
            if (response == null && !haveNewData)
            {
                throw new FonaCommandTimeout();
            }

            return response;
        }

        #endregion

        #region Serial Helpers

        private readonly object _responseQueueLock = new object();
        private readonly ArrayList _responseQueue = new ArrayList();
        private readonly AutoResetEvent _responseReceived = new AutoResetEvent(false);
        private string _buffer;

        private void PortOnDataReceived(object sender, SerialDataReceivedEventArgs serialDataReceivedEventArgs)
        {
            if (serialDataReceivedEventArgs.EventType == SerialData.Chars)
            {
                string newInput = ReadExisting();
                if (newInput != null && newInput.Length > 0)
                {
                    _buffer += newInput;
                    var idxNewline = _buffer.IndexOf('\n');
                    while (idxNewline!=-1)
                    {
                        var line = _buffer.Substring(0, idxNewline);
                        _buffer = _buffer.Substring(idxNewline + 1);
                        if (line[line.Length - 1] == '\r')
                            line = line.Substring(0, line.Length - 1);
                        if (line.ToUpper() == "RING")
                        {
                            // If we received a line with 'RING' on it, and the hardware ring pin
                            //   is not enabled, then raise a ring event. In this case, the RING
                            //   indication may be raised several times for a single incoming call.
                            //   Otherwise, eat the RING string because it will just confuse us
                            if (!this.HardwareRingIndicationEnabled)
                            {
                                RaiseTextRingEvent();
                            }
                        }
                        else
                        {
                            if (line.Length > 0)
                            {
                                lock (_responseQueueLock)
                                {
                                    _responseQueue.Add(line);
                                    _responseReceived.Set();
                                }
                            }
                        }

                        // See if we have another line buffered
                        idxNewline = _buffer.IndexOf('\n');
                    }
                }
            }
        }

        private void RaiseTextRingEvent()
        {
            if (this.Ringing != null)
            {
                this.Ringing(this,new RingingEventArgs(DateTime.UtcNow));
            }
        }

        private byte[] ReadExistingBinary()
        {
            int arraySize = _port.BytesToRead;

            byte[] received = new byte[arraySize];

            _port.Read(received, 0, arraySize);

            return received;
        }

        /// <summary>
        /// Reads all immediately available bytes, based on the encoding, in both the stream and the input buffer of the SerialPort object.
        /// </summary>
        /// <returns>String</returns>
        private string ReadExisting()
        {
            try
            {
                return new string(Encoding.UTF8.GetChars(this.ReadExistingBinary()));
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void DiscardBufferedInput()
        {
            lock (_responseQueueLock)
            {
                _port.DiscardInBuffer();
                _responseQueue.Clear();
                _buffer = "";
                _responseReceived.Reset();
            }
        }

        private void Write(string txt)
        {
            _port.Write(Encoding.UTF8.GetBytes(txt), 0, txt.Length);
        }

        private void WriteLine(string txt)
        {
            this.Write(txt + "\r\n");
        }

        #endregion

    }
}
