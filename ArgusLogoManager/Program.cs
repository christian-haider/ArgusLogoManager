using Microsoft.Extensions.Configuration;
using SvnClient;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Xml;

namespace ArgusLogoManager
{
    internal class Program
    {
        private DirectoryInfo LOGO_SRC_DIR = null;
        private DirectoryInfo LOGO_TARGET_DIR = null;
        private string DB_CONNECTION_STRING = null;

        private List<(int ChannelType, string DisplayName)> argusTVChannels = new List<(int ChannelType, string DisplayName)>();

        private readonly Dictionary<string, string> channelMapping = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
        {
			// start tv
			{ "ARD-alpha HD", "ARD-alpha" },
            { "Bloomberg Europe TV", "Bloomberg" },
            { "Channel21 HD", "Channel21" },
            { "Comedy Central Austria", "Comedy Central/VIVA HD" },
            { "DW English", "Deutsche Welle TV" },
            { "EURONEWS FRENCH SD", "EuroNews" },
            { "Folx TV", "Folx.TV" },
            { "France 24 (in English)", "France 24 (frz)" },
			//{ "Genius Plus", "xxxx" },
			//{ "Immer etwas Neues TV", "xxxx" },
			{ "NHK WORLD-JPN", "NHK World" },
            { "NITRO Austria", "NITRO" },
            { "QVC Style HD", "QVC Beauty" },
			//{ "QVC2 HD", "xxxx" },
			{ "Radio Bremen TV", "Radio Bremen TV" },
            { "SR Fernsehen HD", "SR Fernsehen" },
            { "Sat 1 Gold Austria", "SAT.1 Gold Österreich" },
            { "Schau TV HD", "Schau TV" },
			//{ "Sparhandy TV 2 HD", "xxxx" },
			//{ "Sparhandy TV", "xxxx" },
			{ "TLC Austria", "TLC" },
			//{ "TV1 OOE", "xxxx" },
			//{ "a.tv", "xxxx" },
			{ "n-tv Austria", "n-tv" },
			//{ "oe24.TV HD", "xxxx" },
			//{ "tm3", "xxxx" },

			// start radio
			//{ "BR Heimat", "xxxx" },
			//{ "Bremen Zwei", "xxxx" },
			//{ "COSMO", "xxxx" },
			//{ "DRadio DokDeb", "xxxx" },
			//{ "Dlf Kultur", "xxxx" },
			//{ "Dlf Nova", "xxxx" },
			//{ "ERF Plus", "xxxx" },
			//{ "LTC", "xxxx" },
			//{ "MDR AKTUELL", "xxxx" },
			//{ "MDR KULTUR", "xxxx" },
			//{ "MDR S-ANHALT MD", "xxxx" },
			//{ "MDR SACHSEN DD", "xxxx" },
			//{ "NDR 1 Nieders. HAN", "xxxx" },
			//{ "NDR 1 Radio MV SN", "xxxx" },
			//{ "NDR 2 NDS", "xxxx" },
			//{ "NDR Blue", "xxxx" },
			//{ "NDR Info NDS", "xxxx" },
			//{ "NDR1 Welle Nord KI", "xxxx" },
			//{ "SWR Aktuell", "xxxx" },
			//{ "UHD1 by ASTRA / HD+", "xxxx" },
			//{ "WDR 2 Rheinland", "xxxx" },
		};

        private static NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();
        protected IConfigurationRoot Configuration { get; }

        public Program()
        {
            var builder = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddUserSecrets<Program>();
            Configuration = builder.Build();

            LOGO_SRC_DIR = new DirectoryInfo(Configuration["Logos.Source.SVN"]);
            LOGO_TARGET_DIR = new DirectoryInfo(Configuration["Logos.Target.DIR"]);
            DB_CONNECTION_STRING = Configuration["DB.ConnectionString"];
        }

        // based on https://github.com/bhank/SVNCompleteSync
        private static void Main(string[] args)
        {
            try
            {
                var program = new Program();
                program.DownloadLogosFromMp();
                program.LoadChannelsFromArgusTv();
                program.MatchLogos();
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        private string NormalizeLogoFilename(string fileName)
        {
            //https://www.argus-tv.com/wiki/index.php?title=Channel_Logos
            //TODO check for . on end
            return fileName.Replace("\\", "_").Replace("/", "_").Replace(":", "_").Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
        }

        private void MatchLogos()
        {
            if (LOGO_TARGET_DIR.Exists)
            {
                LOGO_TARGET_DIR.Delete(true);
                LOGO_TARGET_DIR.Create();
            }
            else
            {
                LOGO_TARGET_DIR.Create();
            }

            var logoMapping = new XmlDocument();
            logoMapping.Load(Path.Combine(LOGO_SRC_DIR.FullName, "LogoMapping.xml"));
            XmlNodeList logoItemList = logoMapping.SelectNodes("//Item");

            foreach (var channel in argusTVChannels)
            {
                string searchChannelName = channel.DisplayName;
                if (channelMapping.ContainsKey(searchChannelName))
                {
                    searchChannelName = channelMapping[searchChannelName];
                }

                var itemNodes = logoItemList.Cast<XmlNode>().Where(n => n.Attributes["Name"].InnerText.ToLowerInvariant() == searchChannelName.ToLowerInvariant()).ToList();

                bool found = false;
                if (itemNodes != null && itemNodes.Count > 1)
                {
                    Log.Error($"ERR Found more the 1 result for '{channel}'");
                }

                foreach (XmlNode itemNode in itemNodes)
                {
                    if (found)
                        continue;

                    var filenameSrc = itemNode.ParentNode.SelectSingleNode("File").InnerText;

                    string srcFilename = Path.Combine(LOGO_SRC_DIR.FullName, channel.ChannelType == 0 ? "tv" : "radio", filenameSrc);
                    string targetFilename = Path.Combine(LOGO_TARGET_DIR.FullName, NormalizeLogoFilename(channel.DisplayName) + ".png");

                    File.Copy(srcFilename, targetFilename);
                    Log.Info($"Channel '{channel}' Src '{srcFilename}' Target '{targetFilename}'");
                    found = true;
                }

                if (!found)
                {
                    Log.Error($"ERR No result for '{channel}'");
                }
            }
        }

        private void DownloadLogosFromMp()
        {
            if (!LOGO_SRC_DIR.Exists)
            {
                LOGO_SRC_DIR.Create();
            }

            var parameters = new Parameters()
            {
                Command = Command.CheckoutUpdate,
                Url = "https://subversion.assembla.com/svn/mediaportal.LogoPack-Germany/trunk",
                Cleanup = true,
                Mkdir = false,
                Revert = true,
                DeleteUnversioned = true,
                TrustServerCert = true,
                Verbose = true,
                Path = LOGO_SRC_DIR.FullName
            };

            SvnClient.SvnClient.CheckoutUpdate(parameters);
        }

        private void LoadChannelsFromArgusTv()
        {
            //https://stackoverflow.com/questions/6073382/read-sql-table-into-c-sharp-datatable
            string query = "SELECT ChannelType, DisplayName FROM [ArgusTV].[dbo].[Channel] WHERE GuideChannelId IS NOT NULL ORDER BY ChannelType, DisplayName";

            using (SqlConnection sqlConn = new SqlConnection(DB_CONNECTION_STRING))
            {
                using (SqlCommand cmd = new SqlCommand(query, sqlConn))
                {
                    sqlConn.Open();
                    // https://stackoverflow.com/questions/1464883/how-can-i-easily-convert-datareader-to-listt
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            argusTVChannels.Add((reader.GetInt32(0), reader.GetString(1)));
                        }

                        //argusTVChannels = reader.Cast<IDataRecord>().Select(x => new List<(int ChannelType, string DisplayName)>(){
                        //	(x.GetInt32(0), x.GetString(1))
                        //}).ToList();
                    }
                }
            }
        }
    }
}