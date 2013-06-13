﻿using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Xml.XPath;
using System.Net;
using Tamir.SharpSsh.jsch;
using Tamir.SharpSsh;
using System.Timers;
using System.Net.Sockets;

namespace Litle.Sdk
{
    public class Communications
    {
        virtual public string HttpPost(string xmlRequest, Dictionary<String, String> config)
        {
            string uri = config["url"];
            System.Net.ServicePointManager.Expect100Continue = false;
            System.Net.WebRequest req = System.Net.WebRequest.Create(uri);
            if ("true".Equals(config["printxml"]))
            {
                Console.WriteLine(xmlRequest);
            }
            req.ContentType = "text/xml";
            req.Method = "POST";
            if (config.ContainsKey("proxyHost") && config["proxyHost"].Length > 0 && config.ContainsKey("proxyPort") && config["proxyPort"].Length > 0)
            {
                WebProxy myproxy = new WebProxy(config["proxyHost"], int.Parse(config["proxyPort"]));
                myproxy.BypassProxyOnLocal = true;
                req.Proxy = myproxy;
            }

            // submit http request
            using (var writer = new StreamWriter(req.GetRequestStream()))
            {
                writer.Write(xmlRequest);
            }

            // read response
            System.Net.WebResponse resp = req.GetResponse();
            if (resp == null)
            {
                return null;
            }
            string xmlResponse;
            using (var reader = new System.IO.StreamReader(resp.GetResponseStream()))
            {
                xmlResponse = reader.ReadToEnd().Trim();
            }
            if ("true".Equals(config["printxml"]))
            {
                Console.WriteLine(xmlResponse);
            }
            return xmlResponse;
        }

        virtual public string socketStream(string xmlRequestFilePath, string xmlResponseDestinationDirectory, Dictionary<String, String> config)
        {
            string url = config["onlineBatchUrl"];
            int port = Int32.Parse(config["onlineBatchPort"]);
            IPHostEntry hostEntry = Dns.GetHostEntry(url);
            Socket socket = null;

            //PROXY?
            if (config.ContainsKey("proxyHost") && config["proxyHost"].Length > 0 && config.ContainsKey("proxyPort") && config["proxyPort"].Length > 0)
            {
                //WebProxy myproxy = new WebProxy(config["proxyHost"], int.Parse(config["proxyPort"]));
                //myproxy.BypassProxyOnLocal = true;
                //req.Proxy = myproxy;
            }

            foreach (IPAddress ipAddress in hostEntry.AddressList)
            {
                IPEndPoint ipEndPoint = new IPEndPoint(ipAddress, port);
                Socket tempSocket = new Socket(ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                tempSocket.Connect(ipAddress, port);

                if (tempSocket.Connected)
                {
                    socket = tempSocket;
                }
            }

            if (socket == null)
            {
                throw new LitleOnlineException("Error establishing a network socket");
            }

            using (FileStream readFileStream = new FileStream(xmlRequestFilePath, FileMode.Open))
            using (StreamReader reader = new StreamReader(readFileStream))
            {
                int charsRead = 0;
                char[] charBuffer = new char[1024];

                do
                {
                    charsRead = reader.Read(charBuffer, 0, charBuffer.Length);

                    if ("true".Equals(config["printxml"]))
                    {
                        Console.Write(charBuffer);
                    }

                    socket.Send(Encoding.UTF8.GetBytes(charBuffer));
                } while (charsRead > 0);
            }

            string batchName = Path.GetFileName(xmlRequestFilePath);
            string destinationDirectory = Path.GetDirectoryName(xmlResponseDestinationDirectory);
            if (!Directory.Exists(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            using (FileStream writeFileStream = new FileStream(xmlResponseDestinationDirectory + batchName, FileMode.Create))
            using (StreamWriter writer = new StreamWriter(writeFileStream))
            {
                char[] charBuffer = new char[1024];
                byte[] byteBuffer = new byte[1024 * sizeof(char)];
                int bytesRead = 0;            

                do
                {
                    bytesRead = socket.Receive(byteBuffer, byteBuffer.Length, 0);
                    charBuffer = Encoding.UTF8.GetChars(byteBuffer);

                    if ("true".Equals(config["printxml"]))
                    {
                        Console.Write(charBuffer);
                    }

                    writer.Write(charBuffer);
                } while (bytesRead > 0);
            }

            return xmlResponseDestinationDirectory + batchName;
        }

        virtual public void FtpDropOff(string filePath, Dictionary<String, String> config)
        {
            ChannelSftp channelSftp = null;
            Channel channel;

            string currentPath = Environment.CurrentDirectory.ToString();
            string parentPath = Directory.GetParent(currentPath).ToString();

            string url = config["sftpUrl"];
            string username = config["sftpUsername"];
            string password = config["sftpPassword"];
            string knownHostsFile = parentPath + "\\" + config["knownHostsFile"];
            string fileName = Path.GetFileName(filePath);

            JSch jsch = new JSch();
            jsch.setKnownHosts(knownHostsFile);

            Session session = jsch.getSession(username, url);
            session.setPassword(password);

            try
            {
                session.connect();

                channel = session.openChannel("sftp");
                channel.connect();
                channelSftp = (ChannelSftp)channel;
            }
            catch (SftpException e)
            {
                if (e.message != null)
                {
                    throw new LitleOnlineException(e.message);
                }
                else
                {
                    throw new LitleOnlineException("Error occured while attempting to establish an SFTP connection");
                }
            }

            try
            {
                channelSftp.put(filePath, "inbound/" + fileName, ChannelSftp.OVERWRITE);
                channelSftp.rename("inbound/" + fileName, "inbound/" + fileName + ".asc");
            }
            catch (SftpException e)
            {
                if (e.message != null)
                {
                    throw new LitleOnlineException(e.message);
                }
                else
                {
                    throw new LitleOnlineException("Error occured while attempting to upload and save the file to SFTP");
                }
            }

            channelSftp.quit();

            session.disconnect();
        }

        virtual public void FtpPoll(string fileName, int timeout, Dictionary<string, string> config)
        {
            ChannelSftp channelSftp = null;
            Channel channel;

            string currentPath = Environment.CurrentDirectory.ToString();
            string parentPath = Directory.GetParent(currentPath).ToString();

            string url = config["sftpUrl"];
            string username = config["sftpUsername"];
            string password = config["sftpPassword"];
            string knownHostsFile = parentPath + "\\" + config["knownHostsFile"];

            JSch jsch = new JSch();
            jsch.setKnownHosts(knownHostsFile);

            Session session = jsch.getSession(username, url);
            session.setPassword(password);

            try
            {
                session.connect();

                channel = session.openChannel("sftp");
                channel.connect();
                channelSftp = (ChannelSftp)channel;
            }
            catch (SftpException e)
            {
                if (e.message != null)
                {
                    throw new LitleOnlineException(e.message);
                }
                else
                {
                    throw new LitleOnlineException("Error occured while attempting to establish an SFTP connection");
                }
            }

            //check if file exists
            SftpATTRS sftpATTRS = null;
            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
            do
            {
                try
                {
                    sftpATTRS = channelSftp.lstat("outbound/" + fileName);
                }
                catch
                {
                }
            } while (sftpATTRS == null && stopWatch.Elapsed.TotalMilliseconds <= timeout);
        }

        virtual public void FtpPickUp(string destinationFilePath, Dictionary<String, String> config, string fileName)
        {
            ChannelSftp channelSftp = null;
            Channel channel;

            string currentPath = Environment.CurrentDirectory.ToString();
            string parentPath = Directory.GetParent(currentPath).ToString();

            string url = config["sftpUrl"];
            string username = config["sftpUsername"];
            string password = config["sftpPassword"];
            string knownHostsFile = parentPath + "\\" + config["knownHostsFile"];

            JSch jsch = new JSch();
            jsch.setKnownHosts(knownHostsFile);

            Session session = jsch.getSession(username, url);
            session.setPassword(password);

            try
            {
                session.connect();

                channel = session.openChannel("sftp");
                channel.connect();
                channelSftp = (ChannelSftp)channel;
            }
            catch (SftpException e)
            {
                if (e.message != null)
                {
                    throw new LitleOnlineException(e.message);
                }
                else
                {
                    throw new LitleOnlineException("Error occured while attempting to establish an SFTP connection");
                }
            }

            try
            {
                channelSftp.get("outbound/" + fileName + ".asc", destinationFilePath);
                channelSftp.rm("outbound/" + fileName + ".asc");
            }
            catch (SftpException e)
            {
                if (e.message != null)
                {
                    throw new LitleOnlineException(e.message);
                }
                else
                {
                    throw new LitleOnlineException("Error occured while attempting to retrieve and save the file from SFTP");
                }
            }

            channelSftp.quit();

            session.disconnect();

        }

        public struct SshConnectionInfo
        {
            public string Host;
            public string User;
            public string Pass;
            public string IdentityFile;
        }
    }
}