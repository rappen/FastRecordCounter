﻿using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Fic.XTB.FastRecordCounter.Model;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Office.Interop.Excel;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Tooling.Connector;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace Fic.XTB.FastRecordCounter
{
    public partial class FastRecordCounter : PluginControlBase, IGitHubPlugin
    {
        #region GitHub Info
        public string RepositoryName => "FastRecordCounter";
        public string UserName => "DynamicsNinja";
        #endregion

        public string CurrentOrg;
        public Settings Settings;
        public List<string> ColumnHeaders = new List<string> { "Display Name", "Schema Name", "Count" };

        public FastRecordCounter()
        {
            InitializeComponent();
        }

        private void MyPluginControl_Load(object sender, EventArgs e)
        {
            if (!SettingsManager.Instance.TryLoad(GetType(), out Settings))
            {
                Settings = new Settings();

                LogWarning("Settings not found => a new settings file has been created!");
            }
            else
            {
                LogInfo("Settings found and loaded");
            }
        }

        private void tsbClose_Click(object sender, EventArgs e)
        {
            CloseTool();
        }

        private void FastRecordCounter_OnCloseTool(object sender, EventArgs e)
        {
            SaveSettings();
        }

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);

            CurrentOrg = detail?.Organization;

            if (Settings != null && detail != null)
            {
                Settings.LastUsedOrganizationWebappUrl = detail.WebApplicationUrl;
                LogInfo("Connection has changed to: {0}", detail.WebApplicationUrl);
            }

            var version = new Version(detail?.OrganizationVersion ?? string.Empty);

            tsbCount.Enabled = version >= new Version(9, 0);

            if (!tsbCount.Enabled)
            {
                MessageBox.Show(
                    @"Fast record counting is available only on instances that are on v9+",
                    @"Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        }

        private void FastRecordCounter_ConnectionUpdated(object sender, ConnectionUpdatedEventArgs e)
        {
            ExecuteMethod(LoadEntities);
        }

        private void LoadEntities()
        {
            WorkAsync(new WorkAsyncInfo("Loading entities...",
                (eventargs) =>
                {
                    var eqe = new EntityQueryExpression
                    {
                        Properties = new MetadataPropertiesExpression("LogicalName", "DisplayName")
                    };
                    var req = new RetrieveMetadataChangesRequest
                    {
                        Query = eqe,
                        ClientVersionStamp = null
                    };
                    eventargs.Result = Service.Execute(req) as RetrieveMetadataChangesResponse;
                })
            {
                PostWorkCallBack = (completedargs) =>
                {
                    if (completedargs.Error != null)
                    {
                        MessageBox.Show(completedargs.Error.Message);
                    }
                    else
                    {
                        if (!(completedargs.Result is RetrieveMetadataChangesResponse response)) { return; }

                        var entites = response.EntityMetadata;

                        var currentSettings = Settings.FrcSettings.FirstOrDefault(s => s.Organization == CurrentOrg);

                        dgvEntities.Rows.Clear();

                        foreach (var entity in entites)
                        {
                            var rowId = dgvEntities.Rows.Add();
                            var row = dgvEntities.Rows[rowId];

                            row.Cells[GridColumn.Selected].Value = currentSettings?.SelectedEntities.Contains(entity.LogicalName) ?? false;
                            row.Cells[GridColumn.DisplayName].Value = entity.DisplayName.LocalizedLabels.FirstOrDefault()?.Label ?? entity.LogicalName;
                            row.Cells[GridColumn.SchemaName].Value = entity.LogicalName;
                        }

                        CountVisibleEntities();
                        CountSelected();
                    }
                }
            });
        }

        private void tsbCount_Click(object sender, EventArgs e)
        {
            var countSelected = CountSelected();

            if (countSelected == 0)
            {
                MessageBox.Show(
                    @"There are no entities selected. Please select at least one entity to proceed with counting.",
                    @"Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            else
            {
                ExecuteMethod(CountRecords);

            }
        }

        private void CountRecords()
        {
            WorkAsync(new WorkAsyncInfo("Counting records...",
                (eventargs) =>
                {
                    var requestsCollection = new OrganizationRequestCollection();

                    foreach (DataGridViewRow row in dgvEntities.Rows)
                    {
                        var isSelected = (bool)row.Cells[GridColumn.Selected].Value;
                        if (!isSelected) { continue; }

                        var entityName = row.Cells[GridColumn.SchemaName].Value.ToString();

                        requestsCollection.Add(
                            new RetrieveTotalRecordCountRequest
                            {
                                EntityNames = new[] { entityName }
                            }
                        );
                    }

                    var chunks = Chunk(requestsCollection, 100);

                    var options = new ParallelOptions { MaxDegreeOfParallelism = 10 };

                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    Parallel.ForEach(chunks, options, chunk =>
                    {
                        var svc = ((CrmServiceClient)Service).Clone() ?? Service;

                        var requests = new OrganizationRequestCollection();
                        requests.AddRange(chunk);

                        var countRequest = new ExecuteMultipleRequest()
                        {
                            Requests = requests,
                            Settings = new ExecuteMultipleSettings
                            {
                                ContinueOnError = true,
                                ReturnResponses = true
                            }
                        };

                        var results = (ExecuteMultipleResponseItemCollection)svc.Execute(countRequest).Results["Responses"];

                        for (var index = 0; index < results.Count; index++)
                        {
                            var result = results[index];

                            if (result.Fault != null)
                            {
                                var entityName = ((RetrieveTotalRecordCountRequest)countRequest.Requests[index]).EntityNames.FirstOrDefault();

                                SetCount(entityName, -1, result.Fault.Message);
                            }
                            else
                            {
                                var countResponse = ((EntityRecordCountCollection)result.Response.Results.FirstOrDefault().Value).FirstOrDefault();

                                var entityName = countResponse.Key;
                                var count = (int)countResponse.Value;

                                SetCount(entityName, count, null);
                            }
                        }
                    });

                    watch.Stop();
                    var ms = (decimal)watch.ElapsedMilliseconds;
                })
            {
                PostWorkCallBack = (completedargs) =>
                {
                    if (completedargs.Error != null)
                    {
                        MessageBox.Show(completedargs.Error.Message);
                    }
                }
            });
        }

        public static List<List<T>> Chunk<T>(IEnumerable<T> data, int size)
        {
            return data
                .Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / size)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();
        }

        private void SetCount(string entityName, int count, string error)
        {
            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                if (row.Cells[GridColumn.SchemaName].Value.ToString() != entityName)
                {
                    continue;
                }

                row.Cells[GridColumn.Result].Value = count;
                row.Cells[GridColumn.Result].ErrorText = error;
            };
        }

        private void tsbSelectAll_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                row.Cells[GridColumn.Selected].Value = true;
            }

            CountSelected();
        }

        private void tsbUnselectAll_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                row.Cells[GridColumn.Selected].Value = false;
            }

            CountSelected();
        }

        private void dgvEntities_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            var rowId = e.RowIndex;

            if (rowId == -1) { return; }

            dgvEntities.Rows[rowId].Selected = true;
        }

        private void tstbSearch_TextChanged(object sender, EventArgs e)
        {
            var searchTerm = tstbSearch.Text;

            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                var displayName = row.Cells[GridColumn.DisplayName].Value != null ? row.Cells[GridColumn.DisplayName].Value.ToString() : "";
                var schemaName = row.Cells[GridColumn.SchemaName].Value.ToString();

                row.Visible = displayName.Contains(searchTerm) || schemaName.Contains(searchTerm);
            };

            CountVisibleEntities();
        }

        private void CountVisibleEntities()
        {
            var count = dgvEntities.Rows.Cast<DataGridViewRow>().Count(row => row.Visible);

            tblEntitiesCount.Text = count.ToString();
        }

        private void tstbSearch_Enter(object sender, EventArgs e)
        {
            if (tstbSearch.Text != @"search...") { return; }

            tstbSearch.Text = "";
        }

        private void SaveSettings()
        {
            var selectedEntities = new List<string>();

            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                var isSelected = (bool)row.Cells[GridColumn.Selected].Value;

                if (!isSelected) { continue; }

                var entityName = row.Cells[GridColumn.SchemaName].Value.ToString();
                selectedEntities.Add(entityName);
            }

            var currentOrgSettings = Settings.FrcSettings.FirstOrDefault(s => s.Organization == CurrentOrg);

            if (currentOrgSettings == null)
            {
                Settings.FrcSettings.Add(new FrcSettings
                {
                    Organization = CurrentOrg,
                    SelectedEntities = selectedEntities
                });
            }
            else
            {
                currentOrgSettings.SelectedEntities = selectedEntities;
            }

            SettingsManager.Instance.Save(GetType(), Settings);
        }

        private void dgvEntities_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (dgvEntities.IsCurrentCellDirty && (dgvEntities.CurrentCell.OwningColumn == dgvEntities.Columns[0]))
            {
                dgvEntities.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }

            if (dgvEntities.CurrentCell.OwningColumn == dgvEntities.Columns[0])
            {
                CountSelected();
            }
        }

        private void tsbRefresh_Click(object sender, EventArgs e)
        {
            ExecuteMethod(LoadEntities);
        }

        private int CountSelected()
        {
            var count = dgvEntities.Rows.Cast<DataGridViewRow>().Count(row => (bool)row.Cells[GridColumn.Selected].Value);

            tslSelectedCount.Text = count.ToString();

            return count;
        }

        private void tsbCsv_Click(object sender, EventArgs e)
        {
            var rows = GetRowsWithCount();

            if (rows.Count == 0)
            {
                MessageBox.Show(
                    @"There is nothing to export at the moment. Please count some records first.",
                    @"Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            else
            {
                ExportAsCsv(rows);
            }
        }

        private void ExportAsCsv(List<ExportRow> rows)
        {
            var saveFileDialog = new SaveFileDialog();
            var filter = "CSV file (*.csv)|*.csv| All Files (*.*)|*.*";
            saveFileDialog.Filter = filter;
            saveFileDialog.Title = @"Export as CSV";

            if (saveFileDialog.ShowDialog() != DialogResult.OK) { return; }

            var fileName = saveFileDialog.FileName;
            var writer = new StreamWriter(fileName);

            var header = string.Join(";", ColumnHeaders);

            writer.WriteLine(header);

            foreach (var row in rows)
            {
                writer.WriteLine($"{row.DisplayName};{row.SchemaName};{row.Count}");
            }

            writer.Close();
        }

        private void tsbExcel_Click(object sender, EventArgs e)
        {
            var rows = GetRowsWithCount();

            if (rows.Count == 0)
            {
                MessageBox.Show(
                    @"There is nothing to export at the moment. Please count some records first.",
                    @"Warning",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            else
            {
                ExportAsExcel(rows);
            }
        }

        public void FormatAsTable(Range sourceRange, string tableName, string tableStyleName)
        {
            sourceRange.Worksheet.ListObjects.Add(XlListObjectSourceType.xlSrcRange,
                    sourceRange, Type.Missing, XlYesNoGuess.xlYes, Type.Missing).Name =
                tableName;
            sourceRange.Select();
            sourceRange.Worksheet.ListObjects[tableName].TableStyle = tableStyleName;
        }

        private void ExportAsExcel(List<ExportRow> rows)
        {
            var saveFileDialog = new SaveFileDialog();
            var filter = "Excel file (*.xlsx)|*.xlsx| All Files (*.*)|*.*";
            saveFileDialog.Filter = filter;
            saveFileDialog.Title = @"Export as Excel file";

            if (saveFileDialog.ShowDialog() != DialogResult.OK) { return; }

            WorkAsync(new WorkAsyncInfo("Creating Excel file...",
               (eventargs) =>
               {
                   var excel = new Microsoft.Office.Interop.Excel.Application();
                   var wb = excel.Workbooks.Add();
                   Worksheet sh = wb.Sheets.Add();
                   sh.Name = "Counts";

                   sh.Cells[1, 1].Value2 = ColumnHeaders[0];
                   sh.Cells[1, 2].Value2 = ColumnHeaders[1];
                   sh.Cells[1, 3].Value2 = ColumnHeaders[2];

                   for (var index = 0; index < rows.Count; index++)
                   {
                       var row = rows[index];

                       sh.Cells[index + 2, "A"].Value2 = row.DisplayName;
                       sh.Cells[index + 2, "B"].Value2 = row.SchemaName;
                       sh.Cells[index + 2, "C"].Value2 = row.Count;
                   }

                   var formatRange = sh.Range["C:C"];
                   formatRange.NumberFormat = "#,###,##0";

                   var range = sh.Range["A1", $"C{rows.Count + 1}"]; // or whatever range you want here
                   FormatAsTable(range, "Table1", "TableStyleMedium15");

                   range.Columns.AutoFit();

                   excel.DisplayAlerts = false;
                   wb.SaveAs(saveFileDialog.FileName, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing, XlSaveAsAccessMode.xlNoChange, Type.Missing, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
                   wb.Close(true);
                   excel.Quit();
               })
            {
                PostWorkCallBack = (completedargs) =>
                {
                    if (completedargs.Error != null)
                    {
                        MessageBox.Show(completedargs.Error.Message);
                    }
                }
            });
        }

        private List<ExportRow> GetRowsWithCount()
        {
            var rows = new List<ExportRow>();

            foreach (DataGridViewRow row in dgvEntities.Rows)
            {
                if (!row.Visible) { continue; }

                if (row.Cells[GridColumn.Result].Value != null && (int)row.Cells[GridColumn.Result].Value >= 0)
                {
                    rows.Add(new ExportRow
                    {
                        SchemaName = row.Cells[GridColumn.SchemaName].Value.ToString(),
                        DisplayName = row.Cells[GridColumn.DisplayName].Value.ToString(),
                        Count = (int)row.Cells[GridColumn.Result].Value,
                    });
                }
            }

            var direction = dgvEntities.SortOrder;
            var sortColumn = dgvEntities.SortedColumn?.Name;

            var sorted = rows;
            switch (sortColumn)
            {
                case GridColumn.DisplayName:
                    sorted = direction == SortOrder.Ascending
                        ? rows.OrderBy(r => r.DisplayName).ToList()
                        : rows.OrderByDescending(r => r.DisplayName).ToList();
                    break;
                case GridColumn.SchemaName:
                    sorted = direction == SortOrder.Ascending
                        ? rows.OrderBy(r => r.SchemaName).ToList()
                        : rows.OrderByDescending(r => r.SchemaName).ToList();
                    break;
                case GridColumn.Result:
                    sorted = direction == SortOrder.Ascending
                        ? rows.OrderBy(r => r.Count).ToList()
                        : rows.OrderByDescending(r => r.Count).ToList();
                    break;
            }

            return sorted;
        }
    }
}