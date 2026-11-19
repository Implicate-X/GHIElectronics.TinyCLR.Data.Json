using System;
using System.Collections;
using System.Text;

namespace GHIElectronics.TinyCLR.Data.Json {
    /// <summary>A JSON array — an ordered sequence of <see cref="JToken"/> elements.</summary>
    public class JArray : JToken {
        private readonly JToken[] contents = null!;

        /// <summary>Initializes a new empty JSON array.</summary>
        public JArray() {
        }

        /// <summary>Initializes a new JSON array containing the given elements.</summary>
        public JArray(JToken[] values) => this.contents = values;

        private JArray(Array source, Type elementType, JsonSerializerSettings settings = null!) {
            if (settings == null) {
                settings = new JsonSerializerSettings();
            }

            this.contents = new JToken[ source.Length ];
            for (var i = 0; i < source.Length; ++i) {
                var value = source.GetValue(i);
                if (value == null) {
                    this.contents[ i ] = new JValue();
                    continue;
                }

                var fieldType = value.GetType();
                if (fieldType.IsValueType || fieldType == typeof(string)) {
                    this.contents[ i ] = JValue.Serialize(fieldType, value);
                }
                else {
                    if (fieldType.IsArray) {
                        this.contents[ i ] = JArray.Serialize(fieldType, value, settings);
                    }
                    else {
                        var child = JObject.Serialize(fieldType, value, settings);
                        if (settings.TypeNameHandling == TypeNameHandling.Objects ||
                            (settings.TypeNameHandling == TypeNameHandling.Auto && fieldType != elementType)) {
                            child.Add("$type", new JValue(fieldType.FullName + ", " + fieldType.Assembly.GetName().Name));
                        }
                        this.contents[ i ] = child;
                    }
                }

            }
        }

        /// <summary>Gets the number of elements in the array.</summary>
        public int Length => this.contents.Length;

        /// <summary>Gets the array elements.</summary>
        public JToken[] Items => this.contents;

        /// <summary>Serializes a .NET array into a JSON array.</summary>
        public static JArray Serialize(Type type, object oSource, JsonSerializerSettings settings = null!) {
            if (settings == null) {
                settings = new JsonSerializerSettings();
            }

            return new JArray((Array)oSource, type.GetElementType(), settings);
        }

        /// <summary>Gets the element at the given index.</summary>
        public JToken this[ int i ] => this.contents[ i ];

        /// <summary>Returns the JSON text for this array.</summary>
        public override string ToString() => this.ToString(null!);

        /// <summary>Returns the JSON text for this array using the given formatting options.</summary>
        public override string ToString(JsonSerializationOptions options) {
            this.EnterSerialization(options);
            try {
                var sb = new StringBuilder();

                sb.Append('[');
                this.Indent(true);
                var prefaceLength = 0;

                var first = true;
                foreach (var item in this.contents) {
                    if (!first) {
                        if (sb.Length - prefaceLength > 72) {
                            sb.Append(",");
                            if (JsonConverter.SerializationContext.Options.Indented)
                                sb.AppendLine();
                            prefaceLength = sb.Length;
                        }
                        else {
                            sb.Append(',');
                        }
                    }
                    first = false;
                    sb.Append(item);
                }
                sb.Append(']');
                this.Outdent();
                return sb.ToString();
            }
            finally {
                this.ExitSerialization();
            }
        }

        /// <summary>Gets the number of bytes this array occupies when encoded as BSON.</summary>
        public override int GetBsonSize() {
            var offset = 0;
            this.ToBson(null!, ref offset);
            return offset;
        }

        /// <summary>Gets the number of BSON bytes for this array including the given element name.</summary>
        public override int GetBsonSize(string ename) => 1 + ename.Length + 1 + this.GetBsonSize();

        /// <summary>Writes this array to the buffer as BSON, advancing the offset.</summary>
        public override void ToBson(byte[] buffer, ref int offset) {
            var startingOffset = offset;

            // leave space for the size
            offset += 4;

            for (var i = 0; i < this.contents.Length; ++i) {
                this.contents[ i ].ToBson(i.ToString(), buffer, ref offset);
            }

            // Write the trailing nul
            if (buffer != null)
                buffer[ offset ] = (byte)0;
            ++offset;

            // Write the completed size
            if (buffer != null)
                SerializationUtilities.Marshall(buffer, ref startingOffset, offset - startingOffset);
        }

        /// <summary>Gets the BSON type code for an array.</summary>
        public override BsonTypes GetBsonType() => BsonTypes.BsonArray;

        internal static JArray FromBson(byte[] buffer, ref int offset, InstanceFactory factory = null!) {
            var elementType = (BsonTypes)0;

            var startingOffset = offset;
            var len = (int)SerializationUtilities.Unmarshall(buffer, ref offset, TypeCode.Int32);

            var list = new ArrayList();
            var idx = 0;
            while (offset < startingOffset + len - 1) {
                // get the element type
                var bsonType = (BsonTypes)buffer[ offset++ ];
                if (elementType == (BsonTypes)0)
                    elementType = bsonType;
                if (bsonType != elementType)
                    throw new Exception("all array elements must be of the same type");

                // get the element name
                var idxNul = JToken.FindNul(buffer, offset);
                if (idxNul == -1)
                    throw new Exception("Missing ename terminator");
                var ename = JToken.ConvertToString(buffer, offset, idxNul - offset);
                var elemIdx = int.Parse(ename);
                if (elemIdx != idx)
                    throw new Exception("sparse arrays are not supported");
                ++idx;

                offset = idxNul + 1;

                JToken item = null!;
                switch (bsonType) {
                    case BsonTypes.BsonArray:
                        item = JArray.FromBson(buffer, ref offset, factory);
                        break;
                    case BsonTypes.BsonDocument:
                        item = JObject.FromBson(buffer, ref offset, factory);
                        break;
                    case BsonTypes.BsonNull:
                        item = new JValue();
                        break;
                    case BsonTypes.BsonBoolean:
                    case BsonTypes.BsonDateTime:
                    case BsonTypes.BsonDouble:
                    case BsonTypes.BsonInt32:
                    case BsonTypes.BsonInt64:
                    case BsonTypes.BsonString:
                        item = JValue.FromBson(bsonType, buffer, ref offset);
                        break;
                }
                list.Add(item!);
            }
            if (buffer[ offset++ ] != 0)
                throw new Exception("bad format - missing trailing null on bson document");
            return new JArray((JToken[])list.ToArray(typeof(JToken)));
        }
    }
}
