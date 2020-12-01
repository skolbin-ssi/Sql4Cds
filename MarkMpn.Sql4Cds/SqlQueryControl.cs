﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using AutocompleteMenuNS;
using MarkMpn.Sql4Cds.Engine;
using McTools.Xrm.Connection;
using Microsoft.ApplicationInsights;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using ScintillaNET;
using xrmtb.XrmToolBox.Controls;
using XrmToolBox.Extensibility;

namespace MarkMpn.Sql4Cds
{
    public partial class SqlQueryControl : WeifenLuo.WinFormsUI.Docking.DockContent
    {
        private readonly ConnectionDetail _con;
        private readonly TelemetryClient _ai;
        private readonly Scintilla _editor;
        private readonly string _sourcePlugin;
        private readonly Action<string> _log;
        private string _displayName;
        private string _filename;
        private bool _modified;
        private static int _queryCounter;
        private static ImageList _images;
        private static Icon _sqlIcon;
        private readonly AutocompleteMenu _autocomplete;
        private ToolTip _tooltip;

        static SqlQueryControl()
        {
            _images = new ImageList();
            _images.Images.AddRange(new ObjectExplorer(null, null, null).GetImages().ToArray());

            _sqlIcon = Icon.FromHandle(Properties.Resources.SQLFile_16x.GetHicon());
        }

        public SqlQueryControl(ConnectionDetail con, IAttributeMetadataCache metadata, TelemetryClient ai, Action<WorkAsyncInfo> workAsync, Action<string> setWorkingMessage, Action<Action> executeMethod, Action<MessageBusEventArgs> outgoingMessageHandler, string sourcePlugin, Action<string> log)
        {
            InitializeComponent();
            _displayName = $"Query {++_queryCounter}";
            _modified = true;
            Service = con.ServiceClient;
            Metadata = new MetaMetadataCache(metadata);
            WorkAsync = workAsync;
            SetWorkingMessage = setWorkingMessage;
            ExecuteMethod = executeMethod;
            OutgoingMessageHandler = outgoingMessageHandler;
            _editor = CreateSqlEditor();
            _autocomplete = CreateAutocomplete();
            _sourcePlugin = sourcePlugin;
            _ai = ai;
            _con = con;
            _log = log;
            SyncTitle();

            splitContainer.Panel1.Controls.Add(_editor);
            Icon = _sqlIcon;
        }

        public IOrganizationService Service { get; }
        public IAttributeMetadataCache Metadata { get; }
        public Action<WorkAsyncInfo> WorkAsync { get; }
        public Action<string> SetWorkingMessage { get; }
        public Action<Action> ExecuteMethod { get; }
        public Action<MessageBusEventArgs> OutgoingMessageHandler { get; }
        public string Filename
        {
            get { return _filename; }
            set
            {
                _filename = value;
                _displayName = Path.GetFileName(value);
                _modified = false;
                SyncTitle();
            }
        }

        private void SyncTitle()
        {
            if (_modified)
                Text = $"{_displayName} * ({_con.ConnectionName})";
            else
                Text = $"{_displayName} ({_con.ConnectionName})";
        }

        public void SetFocus()
        {
            _editor.Focus();
        }

        public void Save()
        {
            if (Filename == null)
            {
                using (var save = new SaveFileDialog())
                {
                    save.Filter = "SQL Scripts (*.sql)|*.sql";

                    if (save.ShowDialog() != DialogResult.OK)
                        return;

                    Filename = save.FileName;
                }
            }

            File.WriteAllText(Filename, _editor.Text);
            _modified = false;
            SyncTitle();
        }

        public void InsertText(string text)
        {
            _editor.ReplaceSelection(text);
            _editor.Focus();
        }

        public void Format()
        {
            _ai.TrackEvent("Format SQL");

            var dom = new TSql150Parser(true);
            var fragment = dom.Parse(new StringReader(_editor.Text), out var errors);

            if (errors.Count != 0)
                return;

            new Sql150ScriptGenerator().GenerateScript(fragment, out var sql);
            _editor.Text = sql;
        }

        private Scintilla CreateEditor()
        {
            var scintilla = new Scintilla();

            // Reset the styles
            scintilla.StyleResetDefault();
            scintilla.Styles[Style.Default].Font = "Courier New";
            scintilla.Styles[Style.Default].Size = 10;
            scintilla.StyleClearAll();

            return scintilla;
        }

        private Scintilla CreateTextEditor(bool error)
        {
            var scintilla = CreateEditor();

            scintilla.Lexer = Lexer.Null;
            
            if (error)
                scintilla.Styles[Style.Default].ForeColor = Color.Red;
            
            scintilla.StyleClearAll();

            return scintilla;
        }

        private Scintilla CreateSqlEditor()
        {
            var scintilla = CreateEditor();

            // Set the SQL Lexer
            scintilla.Lexer = Lexer.Sql;

            // Show line numbers
            scintilla.Margins[0].Width = 20;

            // Set the Styles
            scintilla.Styles[Style.LineNumber].ForeColor = Color.FromArgb(255, 128, 128, 128);  //Dark Gray
            scintilla.Styles[Style.LineNumber].BackColor = Color.FromArgb(255, 228, 228, 228);  //Light Gray
            scintilla.Styles[Style.Sql.Comment].ForeColor = Color.Green;
            scintilla.Styles[Style.Sql.CommentLine].ForeColor = Color.Green;
            scintilla.Styles[Style.Sql.CommentLineDoc].ForeColor = Color.Green;
            scintilla.Styles[Style.Sql.Number].ForeColor = Color.Maroon;
            scintilla.Styles[Style.Sql.Word].ForeColor = Color.Blue;
            scintilla.Styles[Style.Sql.Word2].ForeColor = Color.Fuchsia;
            scintilla.Styles[Style.Sql.User1].ForeColor = Color.Gray;
            scintilla.Styles[Style.Sql.User2].ForeColor = Color.FromArgb(255, 00, 128, 192);    //Medium Blue-Green
            scintilla.Styles[Style.Sql.String].ForeColor = Color.Red;
            scintilla.Styles[Style.Sql.Character].ForeColor = Color.Red;
            scintilla.Styles[Style.Sql.Operator].ForeColor = Color.Black;

            // Set keyword lists
            // Word = 0
            scintilla.SetKeywords(0, @"add alter as authorization backup begin bigint binary bit break browse bulk by cascade case catch check checkpoint close clustered column commit compute constraint containstable continue create current cursor cursor database date datetime datetime2 datetimeoffset dbcc deallocate decimal declare default delete deny desc disk distinct distributed double drop dump else end errlvl escape except exec execute exit external fetch file fillfactor float for foreign freetext freetexttable from full function goto grant group having hierarchyid holdlock identity identity_insert identitycol if image index insert int intersect into key kill lineno load merge money national nchar nocheck nocount nolock nonclustered ntext numeric nvarchar of off offsets on open opendatasource openquery openrowset openxml option order over percent plan precision primary print proc procedure public raiserror read readtext real reconfigure references replication restore restrict return revert revoke rollback rowcount rowguidcol rule save schema securityaudit select set setuser shutdown smalldatetime smallint smallmoney sql_variant statistics table table tablesample text textsize then time timestamp tinyint to top tran transaction trigger truncate try union unique uniqueidentifier update updatetext use user values varbinary varchar varying view waitfor when where while with writetext xml go ");
            // Word2 = 1
            scintilla.SetKeywords(1, @"ascii cast char charindex ceiling coalesce collate contains convert current_date current_time current_timestamp current_user floor isnull max min nullif object_id session_user substring system_user tsequal ");
            // User1 = 4
            scintilla.SetKeywords(4, @"all and any between cross exists in inner is join left like not null or outer pivot right some unpivot ( ) * ");
            // User2 = 5
            scintilla.SetKeywords(5, @"sys objects sysobjects ");

            scintilla.Dock = DockStyle.Fill;

            scintilla.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5)
                    Execute(true);

                if (e.KeyCode == Keys.Back && scintilla.SelectedText == String.Empty)
                {
                    var lineIndex = scintilla.LineFromPosition(scintilla.SelectionStart);
                    var line = scintilla.Lines[lineIndex];
                    if (scintilla.SelectionStart == line.Position + line.Length && line.Text.EndsWith("    "))
                    {
                        scintilla.SelectionStart -= 4;
                        scintilla.SelectionEnd = scintilla.SelectionStart + 4;
                        scintilla.ReplaceSelection("");
                        e.Handled = true;
                    }
                }
                if (e.KeyCode == Keys.Space && e.Control)
                {
                    _autocomplete.Show(_editor, true);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            // Auto-indent new lines
            // https://github.com/jacobslusser/ScintillaNET/issues/137
            scintilla.InsertCheck += (s, e) =>
            {
                if (e.Text.EndsWith("\r") || e.Text.EndsWith("\n"))
                {
                    var startPos = scintilla.Lines[scintilla.LineFromPosition(scintilla.CurrentPosition)].Position;
                    var endPos = e.Position;
                    var curLineText = scintilla.GetTextRange(startPos, (endPos - startPos)); // Text until the caret.
                    var indent = Regex.Match(curLineText, "^[ \t]*");
                    e.Text = (e.Text + indent.Value);
                }
            };

            // Define an indicator
            scintilla.Indicators[8].Style = IndicatorStyle.Squiggle;
            scintilla.Indicators[8].ForeColor = Color.Red;

            // Get ready for fill
            scintilla.IndicatorCurrent = 8;

            // Handle changes
            scintilla.TextChanged += (s, e) =>
            {
                if (!_modified)
                {
                    _modified = true;
                    SyncTitle();
                }
            };

            // Rectangular selections
            scintilla.MultipleSelection = true;
            scintilla.MouseSelectionRectangularSwitch = true;
            scintilla.AdditionalSelectionTyping = true;
            scintilla.VirtualSpaceOptions = VirtualSpace.RectangularSelection;

            // Tooltips
            _tooltip = new ToolTip();
            scintilla.DwellStart += (s, e) =>
            {
                _tooltip.Hide(scintilla);

                if (!Settings.Instance.ShowIntellisenseTooltips)
                    return;

                var pos = scintilla.CharPositionFromPoint(e.X, e.Y);
                var text = scintilla.Text;
                var wordEnd = new Regex("\\b").Match(text, pos);

                if (!wordEnd.Success)
                    return;

                EntityCache.TryGetEntities(_con.ServiceClient, out var entities);

                var metaEntities = MetaMetadata.GetMetadata().Select(m => m.GetEntityMetadata());

                if (entities == null)
                    entities = metaEntities.ToArray();
                else
                    entities = entities.Concat(metaEntities).ToArray();

                var suggestions = new Autocomplete(entities, Metadata).GetSuggestions(text, wordEnd.Index - 1, out var currentLength).ToList();
                var exactSuggestions = suggestions.Where(suggestion => suggestion.Text.Length <= wordEnd.Index && text.Substring(wordEnd.Index - suggestion.CompareText.Length, suggestion.CompareText.Length).Equals(suggestion.CompareText, StringComparison.OrdinalIgnoreCase)).ToList();

                if (exactSuggestions.Count == 1)
                {
                    if (!String.IsNullOrEmpty(exactSuggestions[0].ToolTipTitle) && !String.IsNullOrEmpty(exactSuggestions[0].ToolTipText))
                    {
                        _tooltip.ToolTipTitle = exactSuggestions[0].ToolTipTitle;
                        _tooltip.Show(exactSuggestions[0].ToolTipText, scintilla);
                    }
                    else if (!String.IsNullOrEmpty(exactSuggestions[0].ToolTipTitle))
                    {
                        _tooltip.ToolTipTitle = "";
                        _tooltip.Show(exactSuggestions[0].ToolTipTitle, scintilla);
                    }
                    else if (!String.IsNullOrEmpty(exactSuggestions[0].ToolTipText))
                    {
                        _tooltip.ToolTipTitle = "";
                        _tooltip.Show(exactSuggestions[0].ToolTipText, scintilla);
                    }
                }
            };
            scintilla.MouseDwellTime = (int)TimeSpan.FromSeconds(1).TotalMilliseconds;

            return scintilla;
        }

        private AutocompleteMenu CreateAutocomplete()
        {
            var menu = new AutocompleteMenu();
            menu.MinFragmentLength = 1;
            menu.AllowsTabKey = true;
            menu.AppearInterval = 100;
            menu.TargetControlWrapper = new ScintillaWrapper(_editor);
            menu.Font = new Font(_editor.Styles[Style.Default].Font, _editor.Styles[Style.Default].SizeF);
            menu.ImageList = _images;
            menu.MaximumSize = new Size(1000, menu.MaximumSize.Height);

            menu.SetAutocompleteItems(new AutocompleteMenuItems(this));

            return menu;
        }

        class AutocompleteMenuItems : IEnumerable<AutocompleteItem>
        {
            private readonly SqlQueryControl _control;

            public AutocompleteMenuItems(SqlQueryControl control)
            {
                _control = control;
            }

            public IEnumerator<AutocompleteItem> GetEnumerator()
            {
                var pos = _control._editor.CurrentPosition - 1;

                if (pos == 0)
                    yield break;

                var text = _control._editor.Text;
                EntityCache.TryGetEntities(_control._con.ServiceClient, out var entities);

                var metaEntities = MetaMetadata.GetMetadata().Select(m => m.GetEntityMetadata());

                if (entities == null)
                    entities = metaEntities.ToArray();
                else
                    entities = entities.Concat(metaEntities).ToArray();

                var suggestions = new Autocomplete(entities, _control.Metadata).GetSuggestions(text, pos, out var currentLength).ToList();

                if (suggestions.Count == 0)
                    yield break;

                foreach (var suggestion in suggestions)
                    yield return suggestion;
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }
        }

        private Scintilla CreateXmlEditor()
        {
            var scintilla = CreateEditor();

            scintilla.Lexer = Lexer.Xml;

            // Show line numbers
            scintilla.Margins[0].Width = 20;

            scintilla.StyleClearAll();
            scintilla.Styles[Style.LineNumber].ForeColor = Color.FromArgb(255, 128, 128, 128);  //Dark Gray
            scintilla.Styles[Style.LineNumber].BackColor = Color.FromArgb(255, 228, 228, 228);  //Light Gray
            scintilla.Styles[Style.Xml.Attribute].ForeColor = Color.Red;
            scintilla.Styles[Style.Xml.Entity].ForeColor = Color.Red;
            scintilla.Styles[Style.Xml.Comment].ForeColor = Color.Green;
            scintilla.Styles[Style.Xml.Tag].ForeColor = Color.Blue;
            scintilla.Styles[Style.Xml.TagEnd].ForeColor = Color.Blue;
            scintilla.Styles[Style.Xml.DoubleString].ForeColor = Color.DeepPink;
            scintilla.Styles[Style.Xml.SingleString].ForeColor = Color.DeepPink;
            return scintilla;
        }

        public void Execute(bool execute)
        {
            var offset = String.IsNullOrEmpty(_editor.SelectedText) ? 0 : _editor.SelectionStart;

            _editor.IndicatorClearRange(0, _editor.TextLength);

            var sql = _editor.SelectedText;

            if (String.IsNullOrEmpty(sql))
                sql = _editor.Text;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Executing...",
                IsCancelable = execute,
                Work = (worker, args) =>
                {
                    var converter = new Sql2FetchXml(Metadata, Settings.Instance.QuotedIdentifiers);

                    if (Settings.Instance.UseTSQLEndpoint &&
                        execute &&
                        !String.IsNullOrEmpty(((CrmServiceClient)Service).CurrentAccessToken))
                        converter.TSqlEndpointAvailable = true;

                    converter.ColumnComparisonAvailable = new Version(_con.OrganizationVersion) >= new Version("9.1.0.19251");

                    var queries = converter.Convert(sql);

                    var options = new QueryExecutionOptions(Service, worker);

                    if (execute)
                    {
                        foreach (var query in queries)
                        {
                            _ai.TrackEvent("Execute", new Dictionary<string, string> { ["QueryType"] = query.GetType().Name });
                            query.Execute(Service, Metadata, options);
                        }
                    }
                    else
                    {
                        foreach (var query in queries.OfType<IQueryRequiresFinalization>())
                            query.FinalizeRequest(Service, options);
                    }

                    args.Result = queries;
                },
                ProgressChanged = e =>
                {
                    SetWorkingMessage(e.UserState.ToString());
                },
                PostWorkCallBack = (args) =>
                {
                    splitContainer.Panel2.Controls.Clear();

                    if (args.Error is NotSupportedQueryFragmentException err)
                        _editor.IndicatorFillRange(offset + err.Fragment.StartOffset, err.Fragment.FragmentLength);
                    else if (args.Error is QueryParseException parseErr)
                        _editor.IndicatorFillRange(offset + parseErr.Error.Offset, 1);

                    if (args.Error != null)
                    {
                        _ai.TrackException(args.Error, new Dictionary<string, string> { ["Sql"] = sql });
                        _log(args.Error.ToString());

                        var error = CreateTextEditor(true);
                        error.Text = args.Error.Message;
                        error.ReadOnly = true;
                        AddResult(error, false, "Query completed with errors", null, TimeSpan.Zero, null, args.Error);
                        return;
                    }

                    var queries = (Query[])args.Result;

                    foreach (var query in queries.Reverse())
                    {
                        var isMetadata = query.GetType().BaseType.IsGenericType && query.GetType().BaseType.GetGenericTypeDefinition() == typeof(MetadataQuery<,>);

                        if (execute)
                        {
                            Control display = null;
                            var statusText = "Query executed successfully";
                            var method = "SDK";
                            int? rowCount = null;

                            if (query.Result is EntityCollection || query.Result is DataTable)
                            {
                                var entityCollection = query.Result as EntityCollection;
                                var dataTable = query.Result as DataTable;

                                var grid = entityCollection != null ? new CRMGridView() : new DataGridView();

                                grid.AllowUserToAddRows = false;
                                grid.AllowUserToDeleteRows = false;
                                grid.AllowUserToOrderColumns = true;
                                grid.AllowUserToResizeRows = false;
                                grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.WhiteSmoke };
                                grid.BackgroundColor = SystemColors.Window;
                                grid.BorderStyle = BorderStyle.None;
                                grid.CellBorderStyle = DataGridViewCellBorderStyle.None;
                                grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
                                grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
                                grid.EnableHeadersVisualStyles = false;
                                grid.ReadOnly = true;
                                grid.RowHeadersWidth = 24;
                                grid.ShowEditingIcon = false;
                                grid.ContextMenuStrip = gridContextMenuStrip;

                                if (entityCollection != null)
                                {
                                    var crmGrid = (CRMGridView) grid;

                                    crmGrid.EntityReferenceClickable = true;
                                    crmGrid.OrganizationService = Service;
                                    crmGrid.ShowFriendlyNames = Settings.Instance.ShowEntityReferenceNames;
                                    crmGrid.ShowIdColumn = false;
                                    crmGrid.ShowIndexColumn = false;
                                    crmGrid.ShowLocalTimes = Settings.Instance.ShowLocalTimes;
                                    crmGrid.RecordClick += Grid_RecordClick;
                                    crmGrid.CellMouseUp += Grid_CellMouseUp;
                                }

                                if (query is SelectQuery select && (entityCollection != null || isMetadata))
                                {
                                    foreach (var col in select.ColumnSet)
                                    {
                                        var colName = col;

                                        if (grid.Columns.Contains(col))
                                        {
                                            var suffix = 1;
                                            while (grid.Columns.Contains($"{col}_{suffix}"))
                                                suffix++;

                                            var newCol = $"{col}_{suffix}";

                                            if (entityCollection != null)
                                            {
                                                foreach (var entity in entityCollection.Entities)
                                                {
                                                    if (entity.Contains(col))
                                                        entity[newCol] = entity[col];
                                                }
                                            }

                                            colName = newCol;
                                        }

                                        grid.Columns.Add(colName, colName);
                                        grid.Columns[colName].FillWeight = 1;

                                        if (entityCollection == null)
                                            grid.Columns[colName].DataPropertyName = col;
                                    }

                                    if (isMetadata)
                                        grid.AutoGenerateColumns = false;
                                }

                                grid.HandleCreated += (s, e) =>
                                {
                                    if (grid is CRMGridView crmGrid)
                                        crmGrid.DataSource = query.Result;
                                    else
                                        grid.DataSource = query.Result;

                                    grid.AutoResizeColumns();
                                };

                                grid.RowPostPaint += (s, e) =>
                                {
                                    var rowIdx = (e.RowIndex + 1).ToString();

                                    var centerFormat = new System.Drawing.StringFormat()
                                    {
                                        Alignment = StringAlignment.Far,
                                        LineAlignment = StringAlignment.Center,
                                        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
                                        Trimming = StringTrimming.EllipsisCharacter
                                    };

                                    var headerBounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth - 2, e.RowBounds.Height);
                                    e.Graphics.DrawString(rowIdx, this.Font, SystemBrushes.ControlText, headerBounds, centerFormat);
                                };

                                display = grid;

                                if (entityCollection != null)
                                {
                                    rowCount = entityCollection.Entities.Count;
                                }
                                else if (isMetadata)
                                {
                                    rowCount = dataTable.Rows.Count;
                                    method = "Metadata";
                                }
                                else
                                {
                                    rowCount = dataTable.Rows.Count;
                                    method = "TDS";
                                }
                            }
                            else if (query.Result is string msg)
                            {
                                var msgDisplay = CreateTextEditor(false);
                                msgDisplay.Text = msg;
                                msgDisplay.ReadOnly = true;
                                display = msgDisplay;
                            }
                            else if (query.Result is Exception ex)
                            {
                                var msgDisplay = CreateTextEditor(true);

                                if (ex is AggregateException aggregateException)
                                    msgDisplay.Text = String.Join("\r\n", aggregateException.InnerExceptions.Select(e => e.Message));
                                else
                                    msgDisplay.Text = ex.Message;

                                msgDisplay.ReadOnly = true;
                                display = msgDisplay;
                                statusText = "Query completed with errors";
                            }
                            else if (query.Result == null)
                            {
                                statusText = "Query execution cancelled";
                            }

                            if (isMetadata)
                            {
                                var queryDisplay = CreateXmlEditor();
                                queryDisplay.Text = SerializeRequest(query.GetType().GetProperty("Request").GetValue(query));
                                queryDisplay.ReadOnly = true;

                                var metadataInfo = CreatePostProcessingWarning(null, true);

                                if (display == null)
                                {
                                    var panel = new Panel();
                                    panel.Controls.Add(queryDisplay);
                                    panel.Controls.Add(metadataInfo);
                                    display = panel;
                                }
                                else
                                {
                                    var tab = new TabControl();
                                    tab.TabPages.Add("Results");
                                    tab.TabPages.Add("Request");
                                    tab.TabPages[0].Controls.Add(display);
                                    tab.TabPages[1].Controls.Add(queryDisplay);
                                    tab.TabPages[1].Controls.Add(metadataInfo);

                                    display.Dock = DockStyle.Fill;
                                    queryDisplay.Dock = DockStyle.Fill;

                                    display = tab;
                                }
                            }
                            else if (query is FetchXmlQuery fxq && fxq.FetchXml != null)
                            {
                                var xmlDisplay = CreateXmlEditor();
                                xmlDisplay.Text = fxq.FetchXmlString;
                                xmlDisplay.ReadOnly = true;

                                var postWarning = CreatePostProcessingWarning(fxq, false);
                                var toolbar = CreateFXBToolbar(xmlDisplay);

                                if (display == null)
                                {
                                    var panel = new Panel();
                                    panel.Controls.Add(xmlDisplay);

                                    if (postWarning != null)
                                        panel.Controls.Add(postWarning);

                                    panel.Controls.Add(toolbar);
                                    display = panel;
                                }
                                else
                                {
                                    var tab = new TabControl();
                                    tab.TabPages.Add("Results");
                                    tab.TabPages.Add("FetchXML");
                                    tab.TabPages[0].Controls.Add(display);
                                    tab.TabPages[1].Controls.Add(xmlDisplay);

                                    if (postWarning != null)
                                        tab.TabPages[1].Controls.Add(postWarning);

                                    tab.TabPages[1].Controls.Add(toolbar);

                                    display.Dock = DockStyle.Fill;
                                    xmlDisplay.Dock = DockStyle.Fill;

                                    display = tab;
                                }
                            }

                            AddResult(display, queries.Length > 1, statusText, method, query.Duration, rowCount, query.Result);
                        }
                        else if (isMetadata)
                        {
                            var queryDisplay = CreateXmlEditor();
                            queryDisplay.Text = SerializeRequest(query.GetType().GetProperty("Request").GetValue(query));
                            queryDisplay.ReadOnly = true;
                            queryDisplay.Dock = DockStyle.Fill;
                            var metadataInfo = CreatePostProcessingWarning(null, true);
                            var container = new Panel();
                            container.Controls.Add(queryDisplay);
                            container.Controls.Add(metadataInfo);

                            AddResult(container, queries.Length > 1, "Query converted successfully", "Metadata", TimeSpan.Zero, null, queryDisplay.Text);
                        }
                        else if (query is FetchXmlQuery fxq)
                        {
                            var xmlDisplay = CreateXmlEditor();
                            xmlDisplay.Text = fxq.FetchXmlString;
                            xmlDisplay.ReadOnly = true;
                            xmlDisplay.Dock = DockStyle.Fill;
                            var postWarning = CreatePostProcessingWarning(fxq, false);
                            var toolbar = CreateFXBToolbar(xmlDisplay);
                            var container = new Panel();
                            container.Controls.Add(xmlDisplay);

                            if (postWarning != null)
                                container.Controls.Add(postWarning);

                            container.Controls.Add(toolbar);
                            AddResult(container, queries.Length > 1, "Query converted successfully", "FetchXML", TimeSpan.Zero, null, xmlDisplay.Text);
                        }
                    }
                }
            });
        }

        private string SerializeRequest(object request)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = true }))
                {
                    var serializer = new DataContractSerializer(request.GetType());
                    serializer.WriteObject(writer, request);
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private void Grid_CellMouseUp(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (e.ColumnIndex < 0 || e.RowIndex < 0)
                    return;

                var grid = (DataGridView)sender;
                var cell = grid[e.ColumnIndex, e.RowIndex];

                if (!cell.Selected)
                {
                    grid.CurrentCell = cell;
                    grid.ContextMenuStrip.Show(grid, grid.PointToClient(Cursor.Position));
                }
            }
        }

        private void Grid_RecordClick(object sender, CRMRecordEventArgs e)
        {
            // Store the details of what's been clicked
            // Show context menu with Open & Create SELECT options enabled
            if (e.Entity != null && e.Entity.Contains(e.Attribute) && e.Entity[e.Attribute] is EntityReference)
            {
                var grid = (Control)sender;
                gridContextMenuStrip.Show(grid, grid.PointToClient(Cursor.Position));
            }
        }

        private ToolStrip CreateFXBToolbar(Scintilla xmlEditor)
        {
            var toolbar = new ToolStrip();
            toolbar.ImageScalingSize = new Size(24, 24);
            var btn = new ToolStripButton
            {
                Text = "Edit in",
                Image = Properties.Resources.FXB,
                ImageAlign = ContentAlignment.MiddleRight,
                TextAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.TextBeforeImage,
                ToolTipText = "Edit in FetchXML Builder"
            };

            btn.Click += (sender, e) =>
            {
                OutgoingMessageHandler(new MessageBusEventArgs("FetchXML Builder")
                {
                    TargetArgument = xmlEditor.Text
                });
            };
            toolbar.Items.Add(btn);

            if (_sourcePlugin != null)
            {
                var srcBtn = new ToolStripButton
                {
                    Text = "Return to " + _sourcePlugin
                };
                srcBtn.Click += (sender, e) =>
                {
                    OutgoingMessageHandler(new MessageBusEventArgs(_sourcePlugin)
                    {
                        TargetArgument = xmlEditor.Text
                    });
                };
                toolbar.Items.Add(srcBtn);
            }

            return toolbar;
        }

        private Panel CreatePostProcessingWarning(FetchXmlQuery fxq, bool metadata)
        {
            if (!metadata && fxq.Extensions.Count == 0)
                return null;

            var postWarning = new Panel
            {
                BackColor = SystemColors.Info,
                BorderStyle = BorderStyle.None,
                Dock = DockStyle.Top,
                Padding = new Padding(4),
                Height = 24
            };
            var link = new LinkLabel
            {
                Text = $"This query required additional processing. This {(metadata ? "metadata request" : "FetchXML")} gives the required data, but will not give the final results when run outside SQL 4 CDS.",
                ForeColor = SystemColors.InfoText,
                AutoSize = false,
                Dock = DockStyle.Fill
            };
            var linkText = "Learn more";
            link.Text += " " + linkText;
            link.LinkArea = new LinkArea(link.Text.Length - linkText.Length, linkText.Length);
            link.LinkClicked += (s, e) => Process.Start("https://markcarrington.dev/sql-4-cds/additional-processing/");

            postWarning.Controls.Add(link);

            var image = new Bitmap(16, 16);

            using (var g = Graphics.FromImage(image))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(SystemIcons.Warning.ToBitmap(), new Rectangle(Point.Empty, image.Size));
            }
            postWarning.Controls.Add(new PictureBox
            {
                Image = image,
                Height = 16,
                Width = 16,
                Dock = DockStyle.Left
            });
            
            return postWarning;
        }

        private void AddResult(Control control, bool multi, string statusText, string method, TimeSpan duration, int? rowCount, object result)
        {
            var panel = new Panel();
            panel.Controls.Add(control);
            control.Dock = DockStyle.Fill;

            var statusBar = new StatusStrip();
            statusBar.SizingGrip = false;
            statusBar.BackColor = Color.FromArgb(240, 230, 140);
            statusBar.Items.Add(new ToolStripStatusLabel(statusText) { Alignment = ToolStripItemAlignment.Left, Spring = true, TextAlign = ContentAlignment.MiddleLeft, Image = result == null ? Properties.Resources.StatusStop_16x : result is Exception ? Properties.Resources.StatusWarning_16x : Properties.Resources.StatusOK_16x, ImageAlign = ContentAlignment.MiddleLeft });
            statusBar.Items.Add(new ToolStripSeparator() { Alignment = ToolStripItemAlignment.Right });
            statusBar.Items.Add(new ToolStripLabel(new Uri(_con.OrganizationServiceUrl).Host) { Alignment = ToolStripItemAlignment.Right, Image = _con.UseSsl ? Properties.Resources.timeline_lock_on_16x : null, ImageAlign = ContentAlignment.MiddleLeft });
            statusBar.Items.Add(new ToolStripSeparator() { Alignment = ToolStripItemAlignment.Right });
            statusBar.Items.Add(new ToolStripLabel(_con.UserName) { Alignment = ToolStripItemAlignment.Right });

            if (method != null)
            {
                statusBar.Items.Add(new ToolStripSeparator() { Alignment = ToolStripItemAlignment.Right });
                statusBar.Items.Add(new ToolStripLabel(method) { Alignment = ToolStripItemAlignment.Right });
            }

            statusBar.Items.Add(new ToolStripSeparator() { Alignment = ToolStripItemAlignment.Right });
            statusBar.Items.Add(new ToolStripLabel(duration.ToString("hh\\:mm\\:ss")) { Alignment = ToolStripItemAlignment.Right });

            if (rowCount != null)
            {
                statusBar.Items.Add(new ToolStripSeparator() { Alignment = ToolStripItemAlignment.Right });
                statusBar.Items.Add(new ToolStripLabel($"{rowCount:N0} {(rowCount == 1 ? "row" : "rows")}") { Alignment = ToolStripItemAlignment.Right });
            }

            panel.Controls.Add(statusBar);

            panel.Height = splitContainer.Panel2.Height * 2 / 3;
            panel.Dock = multi ? DockStyle.Top : DockStyle.Fill;
            splitContainer.Panel2.Controls.Add(panel);
        }

        private void gridContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            var grid = gridContextMenuStrip.SourceControl as CRMGridView;

            if (grid == null)
            {
                openRecordToolStripMenuItem.Enabled = false;
                createSELECTQueryToolStripMenuItem.Enabled = false;
            }
            else
            {
                var entity = grid.SelectedCells.Count == 1 ? grid.SelectedCellRecords.Entities[0] : null;
                var isEntityReference = false;

                if (entity != null)
                {
                    var attr = grid.SelectedCells[0].OwningColumn.DataPropertyName;

                    if (entity.Contains(attr) && entity[attr] is EntityReference)
                        isEntityReference = true;
                }

                openRecordToolStripMenuItem.Enabled = isEntityReference;
                createSELECTQueryToolStripMenuItem.Enabled = isEntityReference;
            }
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var grid = (DataGridView)gridContextMenuStrip.SourceControl;
            Clipboard.SetDataObject(grid.GetClipboardContent());
        }

        private void copyWithHeadersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var grid = (DataGridView)gridContextMenuStrip.SourceControl;
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
            Clipboard.SetDataObject(grid.GetClipboardContent());
            grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        }

        private void openRecordToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var grid = (CRMGridView)gridContextMenuStrip.SourceControl;
            var entity = grid.SelectedCells.Count == 1 ? grid.SelectedCellRecords.Entities[0] : null;
            var attr = grid.SelectedCells[0].OwningColumn.DataPropertyName;
            var entityReference = entity.GetAttributeValue<EntityReference>(attr);

            // Open record
            var url = new Uri(new Uri(_con.WebApplicationUrl), $"main.aspx?etn={entityReference.LogicalName}&id={entityReference.Id}&pagetype=entityrecord");
            Process.Start(url.ToString());
        }

        private void createSELECTQueryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var grid = (CRMGridView)gridContextMenuStrip.SourceControl;
            var entity = grid.SelectedCells.Count == 1 ? grid.SelectedCellRecords.Entities[0] : null;
            var attr = grid.SelectedCells[0].OwningColumn.DataPropertyName;
            var entityReference = entity.GetAttributeValue<EntityReference>(attr);

            // Create SELECT query
            var metadata = Metadata[entityReference.LogicalName];
            _editor.AppendText("\r\n\r\n");
            var end = _editor.TextLength;
            _editor.AppendText($"SELECT * FROM {entityReference.LogicalName} WHERE {metadata.PrimaryIdAttribute} = '{entityReference.Id}'");
            _editor.SetSelection(_editor.TextLength, end);
        }
    }
}
