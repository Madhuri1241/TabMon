﻿using DataTableWriter.Adapters;
using log4net;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;

namespace DataTableWriter
{
    /// <summary>
    /// Primary point of access to the functionalities of this library.  Provides ability to create dynamic tables on a remote server as well as update existing table schemas to match.
    /// </summary>
    internal static class DbTableManager
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region Public Methods

        /// <summary>
        /// Creates a table on a remote database server and indexes the table based on the config.
        /// </summary>
        /// <param name="adapter">Open adapter to a database.</param>
        /// <param name="schema">The schema of the table to create.</param>
        /// <param name="indexes">A dictionary that contains the indexes to create and whether the indexes are clustered.</param>
        /// <returns>True if table is successfully created.</returns>
        public static bool CreateTable(IDbAdapter adapter, DataTable schema, IDictionary<string, bool> indexes)
        {
            try
            {
                if (!adapter.ExistsTable(schema.TableName))
                {
                    adapter.CreateTable(schema);
                    CreateIndexes(adapter, schema, indexes);
                }
                else
                {
                    Log.Debug(String.Format("Database table '{0}' already exists; skipping creation.", schema.TableName));
                }
                return true;
            }
            catch (DbException ex)
            {
                Log.Error(String.Format("Failed to create database table '{0}': {1}", schema.TableName, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Updates an existing database table to 'match' a schema by adding any missing columns.
        /// </summary>
        /// <param name="adapter">Open adapter to a database.</param>
        /// <param name="schema">The schema to use to update the database.</param>
        /// <returns>True if table is successfully updated to match schema.</returns>
        public static bool UpdateTableToMatchSchema(IDbAdapter adapter, DataTable schema)
        {
            // Check if we need to update the db schema and/or this result schema.
            var dbTable = adapter.GetSchema(schema.TableName);
            if (!schema.SchemaEquals(dbTable))
            {
                try
                {
                    // Insert any missing columns into the database.
                    adapter.AddColumnsToTableToMatchSchema(dbTable.TableName, schema);
                    return true;
                }
                catch (DbException ex)
                {
                    Log.Error("Failed to initialize database writer: " + ex.Message);
                    return false;
                }
            }
            Log.Debug("Database table already matches schema; nothing to update.");
            return true;
        }

        /// <summary>
        /// Updates a schema to 'match' an existing database table by copying it.
        /// </summary>
        /// <param name="adapter">Open adapter to a database.</param>
        /// <param name="schema">The schema to update.</param>
        /// <returns></returns>
        public static bool UpdateSchemaToMatchTable(IDbAdapter adapter, DataTable schema)
        {
            // Copy the DB table's schema back over the top of our in-memory schema to ensure parity.
            try
            {
                // Check to see if overwriting the schema will yield inconsistencies.
                var existingTableSchema = adapter.GetSchema(schema.TableName);
                foreach (DataColumn column in schema.Columns)
                {
                    if (!existingTableSchema.Columns.Contains(column.ColumnName))
                    {
                        Log.Error(String.Format("Cannot update local schema to match database table; column '{0}' exists in the schema, but not the database table.", column.ColumnName));
                        return false;
                    }
                    if (existingTableSchema.Columns[column.ColumnName].DataType != column.DataType)
                    {
                        Log.Error(String.Format("Cannot update local schema to match database table; data types are inconsistent. [Schema='{0}', DbTable='{1}']", column.DataType, existingTableSchema.Columns[column.ColumnName].DataType));
                        return false;
                    }
                    if (existingTableSchema.Columns[column.ColumnName].AllowDBNull != column.AllowDBNull)
                    {
                        Log.Error(String.Format("Cannot update local schema to match database table; inconsistencies in 'AllowDBNull' parameter. [Schema='{0}', DbTable='{1}']", column.AllowDBNull, existingTableSchema.Columns[column.ColumnName].AllowDBNull));
                        return false;
                    }
                }

                // Copy the remote database's schema over the top of our in-memory schema.
                schema = existingTableSchema.Copy();
                Log.Debug(String.Format("Updated internal schema to match database table '{0}'.", schema.TableName));
                return true;
            }
            catch (DbException ex)
            {
                Log.Error(String.Format("Error updating schema '{0}' from database: {1}", schema.TableName, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Indexes a set of columns defined by the dictionary that is passed.
        /// </summary>
        /// <param name="adapter">Open adapter to the database.</param>
        /// <param name="schema">The schema of the table the index will be created on.</param>
        /// <param name="columns">A dictionary where keys are the column names and boolean statements for whether the index is clustered.</param>
        /// <returns>Returns true if the index is created.</returns>
        private static bool CreateIndexes(IDbAdapter adapter, DataTable schema, IDictionary<string,bool> columns)
        {
            if (adapter.ExistsTable(schema.TableName))
            {
                foreach (var column in columns)
                {
                    try
                    {
                        var indexName = column.Key + "_idx";
                        adapter.CreateIndexOnTable(schema.TableName, column.Key, indexName);
                        if (column.Value == true)
                        {
                            adapter.ClusterIndex(schema.TableName, indexName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(String.Format("Error creating index on column '{0}' for table '{1}': {3}", column.Key, schema.TableName, ex.Message));
                        return false;
                    }
                }
            }
            else
            {
                Log.Error(String.Format("Error creating index: Table {0} does not exist", schema.TableName));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Adds indexes to the database if the existing indexes do not match the dictionary passed in.
        /// </summary>
        /// <param name="adapter">Open adapter to the database.</param>
        /// <param name="schema">The schema of the table.</param>
        /// <param name="columns">A dictionary where keys are the column names and boolean statements for whether the index is clustered.</param>
        /// <returns>Returns true if we were successfully able to update the indexes.</returns>
        private static bool AddDbIndexesToMatch(IDbAdapter adapter, DataTable schema, IDictionary<string, bool> columns)
        {
            if (adapter.ExistsTable(schema.TableName))
            {
                try
                {
                    Log.Debug(String.Format("Checking to see if indexes should be added to table '{0}'..", schema.TableName));
                    var dbIndexList = adapter.GetIndexes(schema.TableName.ToString());
                    var indexesToCreate = new Dictionary<string, bool>();
                    var existingIndexes = new List<string>();

                    foreach(var listItem in dbIndexList)
                    {
                        existingIndexes.Add(listItem.IndexName);
                    }

                    foreach (var configEntry in columns)
                    {
                        var indexName  = configEntry.Key + "_idx";
                        if (!existingIndexes.Contains(indexName))
                        {
                            indexesToCreate.Add(configEntry.Key, configEntry.Value);
                        }
                    }
                    CreateIndexes(adapter, schema, indexesToCreate);
                }
                catch
                {
                    Log.Error(String.Format("Unable to update indexes for table '{0}'.", schema.TableName));
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Removes indexes from database if indexes are not in the dictionary.
        /// </summary>
        /// <param name="adapter">Open adapter to the database.</param>
        /// <param name="schema">The schema of the table.</param>
        /// <param name="columns">A dictionary where keys are the column names and boolean statements for whether the index is clustered.</param>
        /// <returns>Returns true if the index(es) were properly removed.</returns>
        private static bool RemoveDBIndexesToMatch(IDbAdapter adapter, DataTable schema, IDictionary<string, bool> columns)
        {
            if (adapter.ExistsTable(schema.TableName))
            {
                try
                {
                    Log.Debug(String.Format("Checking to see if indexes should be removed from table '{0}'..", schema.TableName));
                    var dbIndexes = adapter.GetIndexes(schema.TableName.ToString());
                    var indexesToDrop = new HashSet<string>();

                    foreach (var dbIndex in dbIndexes)
                    {
                        foreach (var indexedColumn in dbIndex.IndexedColumns)
                        {
                            if (!columns.Keys.Contains(indexedColumn))
                            {
                                indexesToDrop.Add(dbIndex.IndexName);
                            }
                        }
                    }
                    
                    foreach (var indexToDrop in indexesToDrop)
                    {
                        adapter.DropIndex(indexToDrop);
                    }
                    
                }
                catch (Exception ex)
                {
                    Log.Error(String.Format("Unable to remove indexes from table '{0}'.", schema.TableName));
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Updates whether or not indexes are clustered.
        /// </summary>
        /// <param name="adapter">Open adapter to the database.</param>
        /// <param name="schema">The schema of the table.</param>
        /// <param name="columns">A dictionary where keys are the column names and boolean statements for whether the index is clustered.</param>
        /// <returns>Returns true if the clusters on indexes were properly updated.</returns>
        private static bool UpdateIndexClusters(IDbAdapter adapter, DataTable schema, IDictionary<string, bool> columns)
        {
            if (adapter.ExistsTable(schema.TableName))
            {
                try
                {
                    Log.Debug(String.Format("Checking to see if clusters on indexes should be updated for table '{0}'..", schema.TableName));
                    var dbIndexList = adapter.GetIndexes(schema.TableName.ToString());
                    var indexesToCheck = from dbColumnName in dbIndexList
                                         from columnNames in dbColumnName.IndexedColumns
                                         join workingColumnName in columns
                                         on columnNames equals workingColumnName.Key
                                         select new
                                         {
                                             dbIndexName = dbColumnName.IndexName,
                                             dbIsCluster = dbColumnName.IsClustered,
                                             indexedColumn = workingColumnName.Key,
                                             workingIsClustered = workingColumnName.Value
                                         };

                    foreach (var index in indexesToCheck)
                    {
                        if (index.dbIsCluster == false && index.workingIsClustered == true)
                        {
                            adapter.ClusterIndex(schema.TableName, index.dbIndexName);
                        }
                        else if (index.dbIsCluster == true && index.workingIsClustered == false)
                        {
                            adapter.DropIndex(index.dbIndexName);
                            adapter.CreateIndexOnTable(schema.TableName, index.indexedColumn, index.dbIndexName);
                        }
                    }
                }
                catch
                {
                    Log.Error(String.Format("Unable to update index clusters for table '{0}'.", schema.TableName));
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Uses an in-memory schema to initialize a database table according to a set of initialization options.
        /// </summary>
        /// <param name="adapter">Open adapter to a database.</param>
        /// <param name="schema">The schema to use to initialize.</param>
        /// <param name="tableInitializationOptions">A set of options to determine initialization behavior.</param>
        public static void InitializeTable(IDbAdapter adapter, DataTable schema, DbTableInitializationOptions tableInitializationOptions)
        {
            if (tableInitializationOptions.CreateTableDynamically)
            {
                CreateTable(adapter, schema, tableInitializationOptions.IndexesToGenerate);
            }
            if (tableInitializationOptions.UpdateDbTableToMatchSchema)
            {
                UpdateTableToMatchSchema(adapter, schema);
            }
            if (tableInitializationOptions.UpdateSchemaToMatchDbTable)
            {
                UpdateSchemaToMatchTable(adapter, schema);
            }
            if (tableInitializationOptions.UpdateIndexes)
            {
                AddDbIndexesToMatch(adapter, schema, tableInitializationOptions.IndexesToGenerate);
                RemoveDBIndexesToMatch(adapter, schema, tableInitializationOptions.IndexesToGenerate);
                UpdateIndexClusters(adapter, schema, tableInitializationOptions.IndexesToGenerate);
            }
        }

        #endregion
    }
}