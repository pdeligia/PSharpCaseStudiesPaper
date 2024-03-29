﻿using Microsoft.Azure.Toolkit.Replication;
using Microsoft.WindowsAzure.Storage.ChainTableInterface;
using Microsoft.WindowsAzure.Storage.RTable;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.WindowsAzure.Storage.ChainTableFactory
{
    public class DefaultChainTableService : IChainTableService
    {
        public DefaultChainTableService(string configFile)
        {
            this.configFileName = configFile;
            StreamReader fs = System.IO.File.OpenText(configFileName);
            string line = fs.ReadLine();

            rTabConfLocs = new List<ConfigurationStoreLocationInfo>();
            rTabDataChain = new List<ReplicaInfo>();

            bool first = true;
            while (!fs.EndOfStream)
            {
                line = fs.ReadLine();
                string[] tokens = line.Split();
                string accountName = tokens[0];
                string accountKey = tokens[1];

                string connStr = String.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", accountName, accountKey);
                if (first)
                {
                    first = false;
                    csa = CloudStorageAccount.Parse(connStr);
                    client = csa.CreateCloudTableClient();
                    rTabConfLocs.Add(new ConfigurationStoreLocationInfo() {
                        StorageAccountName = accountName,
                        StorageAccountKey = accountKey,
                        BlobPath = Constants.RTableConfigurationBlobLocationContainerName + "/myRTableConfig1"
                    });
                }

                rTabDataChain.Add(new ReplicaInfo() {
                    StorageAccountName = accountName,
                    StorageAccountKey = accountKey
                });
            }

            ReplicatedTableConfigurationService rtableConfig = new ReplicatedTableConfigurationService(rTabConfLocs, true);
            rtableConfig.UpdateConfiguration(rTabDataChain, 0);

            fs.Close();
        }

        public IChainTable GetChainTable(string tableId)
        {
            if (tableId.StartsWith("__RTable_"))
            {
                var name = tableId.Substring(9);
                ReplicatedTableConfigurationService rtableConfig = new ReplicatedTableConfigurationService(rTabConfLocs, true);
                ReplicatedTable rTable = new ReplicatedTable(name, rtableConfig);
                return new RTableAdapter(rTable);
            }
            else
                return new ATableAdapter(client.GetTableReference(tableId));
        }


        private string configFileName;
        private CloudStorageAccount csa;
        private CloudTableClient client;

        private List<ConfigurationStoreLocationInfo> rTabConfLocs;
        private List<ReplicaInfo> rTabDataChain;
    }
}
