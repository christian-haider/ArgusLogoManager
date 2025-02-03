using Microsoft.Extensions.Configuration;
using SvnClient;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Xml;
using LibGit2Sharp;

namespace ArgusLogoManager
{
    internal class Program
    {
        private DirectoryInfo LOGO_SRC_DIR_SVN = null;
        private DirectoryInfo LOGO_SRC_DIR_GIT = null;

        private DirectoryInfo LOGO_TARGET_SVN = null;
        private DirectoryInfo LOGO_TARGET_GIT = null;

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
			{ "NHK WORLD-JPN", "NHK World" },
            { "NITRO Austria", "NITRO" },
            { "QVC Style HD", "QVC Beauty" },
            { "SR Fernsehen HD", "SR Fernsehen" },
            { "Sat 1 Gold Austria", "SAT.1 Gold Österreich" },
            { "Schau TV HD", "Schau TV" },
			{ "TLC Austria", "TLC" },
			{ "n-tv Austria", "n-tv" },
            { "gotv neu", "GoTV" },
            { "Juwelo HD", "Juwelo TV" },
            { "Radio Bremen HD", "Radio Bremen TV" },

    		//{ "a.tv", "xxxx" },  
			//{ "Bild Live TV", "xxxx" },  
			//{ "CGTN", "xxxx" },  
			//{ "CNBC HD", "xxxx" },  
			//{ "Genius exklusiv", "xxxx" },  
			//{ "Genius family", "xxxx" },  
			//{ "Genius Plus", "xxxx" },  
			//{ "Handystar TV HD", "xxxx" },  
			//{ "Immer etwas Neues TV", "xxxx" },  
			//{ "oe24.TV HD", "xxxx" },  
			//{ "PULS 24 HD", "xxxx" },  
			//{ "QVC2 HD", "xxxx" },  
			//{ "Starparadies AT", "xxxx" },  
			//{ "TV1 OOE", "xxxx" },  
			//{ "Volksmusik", "xxxx" },  


			// start radio
			{ "Dlf Kultur", "DKultur" },
			{ "ERF Plus", "ERF Radio" },
            { "COSMO", "WDRCosmo" }, // Conflict in https://github.com/Jasmeet181/mediaportal-de-logos/blob/master/LogoMapping.xml Cosmo ES TV/DE Radio

			//{ "BR Heimat", "xxxx" },  
			//{ "Bremen Zwei", "xxxx" },  
			//{ "Die Maus", "xxxx" },  
			//{ "Dlf Nova", "xxxx" },  
			//{ "DRadio DokDeb", "xxxx" },  
			//{ "LTC", "xxxx" },  
			//{ "MDR AKTUELL", "xxxx" },  
			//{ "MDR KULTUR", "xxxx" },  
			//{ "MDR SACHSEN DD", "xxxx" },  
			//{ "MDR S-ANHALT MD", "MDR S-ANHALT" },  
			//{ "NDR 1 Nieders. HAN", "xxxx" },  
			//{ "NDR 1 Radio MV SN", "xxxx" },  
			//{ "NDR 2 NDS", "xxxx" },  
			//{ "NDR Blue", "xxxx" },  
			//{ "NDR Info NDS", "xxxx" },  
			//{ "NDR1 Welle Nord KI", "xxxx" },  
			//{ "QVC2 UHD", "xxxx" },  
			//{ "radioB2  SCHLAGER", "xxxx" },  
			//{ "rbb 88.8", "xxxx" },  
			//{ "rbbKultur", "xxxx" },  
			//{ "SCHLAGER-RADIO", "xxxx" },  
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

            LOGO_SRC_DIR_SVN = new DirectoryInfo(Configuration["Logos.Source.SVN"]);
            LOGO_SRC_DIR_GIT = new DirectoryInfo(Configuration["Logos.Source.GIT"]);

            LOGO_TARGET_SVN = new DirectoryInfo(Configuration["Logos.Target.SVN"]);
            LOGO_TARGET_GIT = new DirectoryInfo(Configuration["Logos.Target.GIT"]);

            DB_CONNECTION_STRING = Configuration["DB.ConnectionString"];
        }

        // based on https://github.com/bhank/SVNCompleteSync
        private static void Main(string[] args)
        {
            try
            {
                var program = new Program();
                program.DownloadLogosFromSources();
                program.LoadChannelsFromArgusTv();
                program.MatchLogos(program.LOGO_SRC_DIR_SVN, program.LOGO_TARGET_SVN);
                program.MatchLogos(program.LOGO_SRC_DIR_GIT, program.LOGO_TARGET_GIT);
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

        private void MatchLogos(DirectoryInfo sourceDir, DirectoryInfo targetDir)
        {
            if (targetDir.Exists)
            {
                targetDir.Delete(true);
                targetDir.Create();
            }
            else
            {
                targetDir.Create();
            }

            var logoMapping = new XmlDocument();
            logoMapping.Load(Path.Combine(sourceDir.FullName, "LogoMapping.xml"));

            Log.Info($"Using source '{sourceDir.FullName}' -> target '{targetDir.FullName}'");

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
                    
                    // Error in https://github.com/Jasmeet181/mediaportal-de-logos/blob/master/LogoMapping.xml UHD1.png does not exist
                    if (filenameSrc == "UHD1.png")
                    {
                        continue;
                    }

                    string srcFilename = Path.Combine(sourceDir.FullName, channel.ChannelType == 0 ? "tv" : "radio", filenameSrc);

                    if (sourceDir == LOGO_SRC_DIR_GIT)
                    {
                        srcFilename = Path.Combine(sourceDir.FullName, channel.ChannelType == 0 ? "TV\\.Light" : "Radio\\.Light", filenameSrc);
                    }

                    string targetFilename = Path.Combine(targetDir.FullName, NormalizeLogoFilename(channel.DisplayName) + ".png");

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

        private void DownloadLogosFromSources()
        {
            if (!LOGO_SRC_DIR_SVN.Exists)
            {
                LOGO_SRC_DIR_SVN.Create();
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
                Path = LOGO_SRC_DIR_SVN.FullName,
                Username = "dummy",
                Password = "dummy"
            };

            SvnClient.SvnClient.CheckoutUpdate(parameters);

            if (!LOGO_SRC_DIR_GIT.Exists)
            {
                LOGO_SRC_DIR_GIT.Create();
            }

            // https://github.com/libgit2/libgit2sharp/wiki/git-clone
            Repository.Clone("https://github.com/Jasmeet181/mediaportal-de-logos.git", LOGO_SRC_DIR_GIT.FullName);
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