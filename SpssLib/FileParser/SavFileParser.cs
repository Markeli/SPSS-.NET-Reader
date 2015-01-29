﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Collections.ObjectModel;
using SpssLib.FileParser.Records;
using System.Data;
using SpssLib.SpssDataset;

namespace SpssLib.FileParser
{
    public class SavFileParser: IDisposable
    {
        public Stream Stream { get; private set; }

        public bool MetaDataParsed { get; private set; }
        public MetaData MetaData { get; private set; }

        private BinaryReader reader;
        private Stream dataRecordStream;
        private long dataStartPosition = 0;

        public SavFileParser(Stream fileStream)
        {
            this.Stream = fileStream;
        }

        public void ParseMetaData()
        {
            var meta = new MetaData();
            reader = new BinaryReader(Stream, Encoding.ASCII);

            var variableRecords = new List<VariableRecord>();
            var valueLabelRecords = new List<ValueLabelRecord>();
            var infoRecords = new InfoRecords();

            //var counter = 0;
            RecordType nextRecordType;
            do
            {
                //Counter is only for hunting bugs to identify the stopper input
                //counter++;
                //Console.WriteLine("{0} {1}", counter, nextRecordType);
                nextRecordType = ReadRecordType();
                switch (nextRecordType)
                {
                    case RecordType.HeaderRecord:
                        meta.HeaderRecord = HeaderRecord.ParseNextRecord(reader);
                        break;
                    case RecordType.VariableRecord:
                        variableRecords.Add(VariableRecord.ParseNextRecord(reader));
                        break;
                    case RecordType.ValueLabelRecord:
                        valueLabelRecords.Add(ValueLabelRecord.ParseNextRecord(reader));
                        break;
                    case RecordType.DocumentRecord:
                        meta.DocumentRecord = DocumentRecord.ParseNextRecord(reader);
                        break;
                    case RecordType.InfoRecord:
                        infoRecords.AllRecords.Add(InfoRecord.ParseNextRecord(reader));
                        break;
                    case RecordType.End:
                        break;
                    default:
                        throw new UnexpectedFileFormatException();
                }
                //nextRecordType = ReadRecordType(reader);
            } while (nextRecordType != RecordType.End);

            meta.VariableRecords = new Collection<VariableRecord>(variableRecords);
            meta.ValueLabelRecords = new Collection<ValueLabelRecord>(valueLabelRecords);

            // Interpret known inforecords:
            infoRecords.ReadKnownRecords(meta.VariableCount);
            meta.InfoRecords = infoRecords;
            this.SysmisValue = meta.InfoRecords.MachineFloatingPointInfoRecord != null 
                                    ? meta.InfoRecords.MachineFloatingPointInfoRecord.SystemMissingValue
                                    : double.MinValue;
            
            // Filler Record
            reader.ReadInt32();

	        try
	        {
				this.dataStartPosition = this.Stream.Position;
	        }
	        catch (NotSupportedException)
	        {
				// Some stream types don't support the Position property
				this.dataStartPosition = 0;
	        }
            
            this.MetaData = meta;
            SetDataRecordStream();
            this.MetaDataParsed = true;
        }

        private RecordType ReadRecordType()
        {
            int recordTypeNum = reader.ReadInt32();
            if (!Enum.IsDefined(typeof (RecordType), recordTypeNum))
            {
                throw new UnexpectedFileFormatException("Record type not recognized: "+recordTypeNum);
            }

            return (RecordType)Enum.ToObject(typeof(RecordType), recordTypeNum);
        }


        private void SetDataRecordStream()
        {
            if (this.MetaData.HeaderRecord.Compressed)
            {
                var bias = this.MetaData.HeaderRecord.Bias;
                var systemMissingValue = MetaData.InfoRecords.MachineFloatingPointInfoRecord != null
                                            ? MetaData.InfoRecords.MachineFloatingPointInfoRecord.SystemMissingValue
                                            : double.MinValue;
                this.dataRecordStream = new Compression.DecompressedDataStream(this.Stream, bias, systemMissingValue);
            }
            else
            {
                this.dataRecordStream = this.Stream;
            }
            this.reader = new BinaryReader(this.dataRecordStream, Encoding.ASCII);
        }

        public IEnumerable<byte[][]> DataRecords
        {
            get
            {
                if (!this.MetaDataParsed)
                {
                    this.ParseMetaData();
                }
                lock (this.Stream)
                {
                    if (dataStartPosition != 0)
                    {
	                    long position;
	                    try
	                    {
		                    position = this.Stream.Position;
	                    }
	                    catch (NotSupportedException ex)
	                    {
							throw new NotSupportedException("Re-reading the data is not allowed on this stream because it doesn't support position.", ex);
	                    }
						if (position != dataStartPosition)
						{
							if (this.Stream.CanSeek)
							{
								this.Stream.Seek(dataStartPosition, 0);
							}
							else
							{
								throw new NotSupportedException("Re-reading the data is not allowed on this stream because it doesn't allow seeking.");
							}
						}
					}
					else if (dataStartPosition != 0)
					{
						// If position could not be read initialy, set as -1 to avoid start reading the records again with out rewinding the stream
						dataStartPosition = -1;
					}

                    byte[][] record = ReadNextDataRecord();
                    while (record != null)
                    {
                        yield return record;
                        record = ReadNextDataRecord();
                    }
                }
            }
        }

        public IEnumerable<IEnumerable<object>> ParsedDataRecords
        {
            get
            {
                foreach (var rawrecord in this.DataRecords)
                {
                    yield return this.RecordToObjects(rawrecord);
                }
            }
        }

        public byte[][] ReadNextDataRecord()
        {
            byte[][] record = new byte[this.MetaData.VariableRecords.Count][];
            for (int i = 0; i < this.MetaData.VariableRecords.Count; i++)
			{
			    record[i]= reader.ReadBytes(Constants.BlockByteSize);
                if (record[i].Length < Constants.BlockByteSize)
                {
                    return null;
                }
			}
            return record;            
        }

		public object ValueToObject(byte[] value, VariableRecord variable)
        {
            if (variable.Type == 0)
            {
                var doubleValue = BitConverter.ToDouble(value, 0);
                if (doubleValue == SysmisValue)
                {
                    return null;
                }
                else
                {
                    return doubleValue;
                }
            }
            else
            {
                return Encoding.ASCII.GetString(value);
            }
        }

        public IEnumerable<object> RecordToObjects(byte[][] record)
        {
            StringBuilder stringBuilder = new StringBuilder();
            bool buildingString = false;
            int variableIndex = 0;
            int stringLength = 0;

            foreach (var variableRecord in this.MetaData.VariableRecords)
            {
                byte[] element = record[variableIndex++];

                if (buildingString && variableRecord.Type != -1)
                {
                    // return the complete string we were building
                    yield return (stringBuilder.ToString()).Substring(0, stringLength);

                    // Clear:
                    stringBuilder.Length = 0;
                    buildingString = false;
                }

                if (variableRecord.Type == 0)
                {
                    // Return numeric value
                    var value =  BitConverter.ToDouble(element, 0);
                    if (value == SysmisValue)
                    {
                        yield return null;
                    }
                    else
                    {
                        yield return value;
                    }
                }
                else
                {
                    if (variableRecord.Type > 0)
                        stringLength = variableRecord.Type;
                        // Add string to string we were building
                    stringBuilder.Append(Encoding.ASCII.GetString(element));
                    buildingString = true;
                }
            }
            // return the complete string we were building
            if (buildingString)
            {
                yield return stringBuilder.ToString();
            }
        }
		
		[Obsolete("Use SpssDataset constructor directly")]
        public SpssDataset.SpssDataset ToSpssDataset()
        {
            return new SpssDataset.SpssDataset(this);
        }

		[Obsolete("Use SpssDataReader constructor directly")]
        public IDataReader GetDataReader()
        {
            return new DataReader.SpssDataReader(this);
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.reader != null)
                {
                    this.reader.Close();
                    this.reader = null;
                }
                if (this.Stream != null)
                {
                    this.Stream.Close();
                    this.Stream = null;
                }
                if (this.dataRecordStream != null)
                {
                    this.dataRecordStream.Close();
                    this.dataRecordStream = null;
                }
            }
        }

        public VariablesCollection Variables
        {
            get
            {
                if (this.MetaData == null)
                    this.ParseMetaData();
                if (this.variables == null)
                {
                    GetVariablesFromRecords();
                }
                return this.variables;
            }
        }

        private VariablesCollection variables;

        private Variable GetVariable(int variableIndex, int dictionaryIndex, FileParser.MetaData metaData)
        {
            var variable = new Variable();
            variable.Index = variableIndex;

            // Get variable record data:
            var variableRecord = metaData.VariableRecords[dictionaryIndex];
            variable.ShortName = variableRecord.Name;
            variable.Label = variableRecord.HasVariableLabel ? variable.Label = variableRecord.Label : null;
	        variable.MissingValueType = variableRecord.MissingValueType;
	        for (int i = 0; i < variableRecord.MissingValues.Count && i < variable.MissingValues.Length; i++)
	        {
		        variable.MissingValues[i] = variableRecord.MissingValues[i];
	        }

            variable.PrintFormat = variableRecord.PrintFormat;
            variable.WriteFormat = variableRecord.WriteFormat;
            variable.Type = variableRecord.Type == 0 ? DataType.Numeric : DataType.Text;
            if (variable.Type == DataType.Text)
            {
                variable.TextWidth = variableRecord.Type;
            }

            // Get value labels:
            var valueLabelRecord = metaData.ValueLabelRecords.FirstOrDefault(record => record.Variables.Contains(dictionaryIndex + 1));
            
            if (valueLabelRecord != null)
            {
                foreach (var label in valueLabelRecord.Labels)
                {
                    variable.ValueLabels.Add(BitConverter.ToDouble(label.Key, 0), label.Value.Replace("\0", string.Empty).Trim());
                }
            }

            // Get display info:
            if (metaData.InfoRecords.VariableDisplayParameterRecord  != null)
            {
                var displayInfo = metaData.InfoRecords.VariableDisplayParameterRecord.VariableDisplayEntries[variableIndex];
                variable.Alignment = displayInfo.Alignment;
                variable.MeasurementType = displayInfo.MeasurementType;
                variable.Width = displayInfo.Width;
            }
            else
            {
                // defaults
                variable.Alignment = Alignment.Right;
                variable.MeasurementType = MeasurementType.Scale;
                variable.Width = variable.PrintFormat.FieldWidth;
            }
            

            // Get (optional) long variable name:
            if (metaData.InfoRecords.LongVariableNamesRecord != null)
            {
                var longNameDictionary = metaData.InfoRecords.LongVariableNamesRecord.LongNameDictionary;
                if (longNameDictionary.ContainsKey(variable.ShortName.Trim()))
                {
                    variable.Name = longNameDictionary[variable.ShortName.Trim()].Trim();
                }
                else
                {
                    variable.Name = variable.ShortName.Trim();
                }
            }
            else
            {
                variable.Name = variable.ShortName.Trim();
            }

            // Todo: digest very long string info.    
            return variable;
        }

        public double SysmisValue { get; set; }

		private void GetVariablesFromRecords()
        {
            this.variables = new VariablesCollection();

            int dictionaryIndex = 0;
            int variableIndex = 0;
            foreach (var variableRecord in this.MetaData.VariableRecords)
            {
                if (variableRecord.Type >= 0)
                {
                    variables.Add(GetVariable(variableIndex, dictionaryIndex, this.MetaData));
                    variableIndex++;
                }
                dictionaryIndex++;
            }
        }
    }
}
