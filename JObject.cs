using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace GHIElectronics.TinyCLR.Data.Json
{
	/// <summary>
	/// Represents a JSON object containing named <see cref="JToken"/> values.
	/// </summary>
	/// <remarks>Property names are matched case-insensitively. Members retain their insertion order when enumerated
	/// and serialized.</remarks>
	public class JObject : JToken
	{
		/// <summary>
		/// Stores the members associated with the object.
		/// </summary>
		private readonly Hashtable members = [];

		/// <summary>
		/// Stores members in order.
		/// </summary>
		private readonly ArrayList orderedMembers = [];

		/// <summary>
		/// Gets or sets the property with the specified name.
		/// </summary>
		/// <param name="name">The name of the property.</param>
		/// <returns></returns>
		/// <exception cref="ArgumentException">The property name does not match the specified name.</exception>
		public JProperty this[ string name ]
		{
			get => ( JProperty )this.members[ name.ToLower() ];
			set
			{
				if( name.ToLower() != value.Name.ToLower() )
					throw new ArgumentException( "index value must match property name" );
				this.AddOrUpdateMember( value.Name.ToLower(), value );
			}
		}

		/// <summary>
		/// Determines whether the collection contains the specified name.
		/// </summary>
		/// <param name="name">The name to search for.</param>
		/// <returns>`true` if the collection contains the name; otherwise, `false`.</returns>
		public bool Contains( string name ) => this.members.Contains( name.ToLower() );

		/// <summary>
		/// Gets the collection of members.
		/// </summary>
		public ICollection Members => this.orderedMembers;

		/// <summary>
		/// Adds a property with the specified name and value.
		/// </summary>
		/// <param name="name">The name of the property.</param>
		/// <param name="value">The value of the property.</param>
		public void Add( string name, JToken value ) => this.AddOrUpdateMember( name.ToLower(), new JProperty( name, value ) );

		/// <summary>
		/// Serializes public instance properties and fields of the specified type to a JSON object.
		/// </summary>
		/// <param name="type">The type whose public instance members are serialized.</param>
		/// <param name="oSource">The object whose members are serialized.</param>
		/// <param name="settings">The settings to use during serialization, or <c>null</c> to use default settings.</param>
		/// <returns>A JSON object containing the serialized members.</returns>
		/// <exception cref="Exception">A getter returns a value whose type is <c>System.Collections.ArrayList</c>.</exception>
		public static JObject Serialize( Type type, object oSource, JsonSerializerSettings settings = null! )
		{
			if( settings == null )
			{
				settings = new JsonSerializerSettings();
			}

			var result = new JObject();
			var methods = type.GetMethods( BindingFlags.Instance | BindingFlags.Public );

			foreach( var m in methods )
			{
				if( !m.IsPublic || m.IsAbstract || m.IsStatic )
					continue;

				if( m.Name.IndexOf( "get_" ) == 0 )
				{
					var name = m.Name.Substring( 4 );
					var methodResult = m.Invoke( oSource, null! );

					if( methodResult == null )
						result.AddOrUpdateMember( name.ToLower(), new JProperty( name, JValue.Serialize( m.ReturnType, null! ) ) );
					else if( m.ReturnType.IsArray )
						result.AddOrUpdateMember( name.ToLower(), new JProperty( name, JArray.Serialize( m.ReturnType, methodResult, settings ) ) );
					else
					{
						if( m.ReturnType.IsValueType || m.ReturnType == typeof( string ) )
						{
							result.AddOrUpdateMember( name.ToLower(), new JProperty( name, JValue.Serialize( m.ReturnType, methodResult ) ) );
						}
						else
						{
							var methodResultType = methodResult.GetType();

							if( methodResultType != null! && methodResultType.FullName != null && methodResultType.FullName.IndexOf( "System.Collections.ArrayList" ) >= 0 )
							{
								throw new Exception( "Not supported " + methodResultType.FullName );
							}

							var child = Serialize( methodResultType!, methodResult, settings );

							if( settings.TypeNameHandling == TypeNameHandling.Objects ||
							   ( settings.TypeNameHandling == TypeNameHandling.Auto && methodResultType != m.ReturnType ) )
							{
								child.Add( "$type", new JValue( methodResultType!.FullName + ", " + methodResultType!.Assembly.GetName().Name ) );
							}

							result.AddOrUpdateMember( name.ToLower(), new JProperty( name, child ) );
						}
					}
				}
			}

			var fields = type.GetFields( BindingFlags.Instance | BindingFlags.Public );

			foreach( var f in fields )
			{
				if( f.FieldType.IsNotPublic )
					continue;

				switch( f.MemberType )
				{
					case MemberTypes.Field:
					case MemberTypes.Property:
						var value = f.GetValue( oSource );

						if( value == null )
						{
							result.AddOrUpdateMember( f.Name.ToLower(), new JProperty( f.Name, JValue.Serialize( f.FieldType, null! ) ) );
						}
						else if( f.FieldType.IsValueType || f.FieldType == typeof( string ) )
						{
							result.AddOrUpdateMember( f.Name.ToLower(),
								new JProperty( f.Name, JValue.Serialize( f.FieldType, value ) ) );
						}
						else
						{
							if( f.FieldType.IsArray )
							{
								result.AddOrUpdateMember( f.Name.ToLower(),
									new JProperty( f.Name, JArray.Serialize( f.FieldType, value, settings ) ) );
							}
							else
							{
								var valueType = value.GetType();
								var child = JObject.Serialize( valueType, value, settings );
								if( settings.TypeNameHandling == TypeNameHandling.Objects ||
									( settings.TypeNameHandling == TypeNameHandling.Auto && valueType != f.FieldType ) )
									child.Add( "$type", new JValue( valueType.FullName + ", " + valueType.Assembly.GetName().Name ) );
								result.AddOrUpdateMember( f.Name.ToLower(), new JProperty( f.Name, child ) );
							}
						}
						break;
					default:
						break;
				}
			}

			JsonConverter.SerializeSpecialMembers( oSource, result, settings );
			return result;
		}

		/// <summary>
		/// Returns a string representation of the object.
		/// </summary>
		/// <returns>A string representation of the object.</returns>
		public override string ToString() => this.ToString( null! );

		/// <summary>
		/// Returns the JSON representation of this object.
		/// </summary>
		/// <param name="options">The options to use when serializing the object.</param>
		/// <returns>The JSON representation of this object.</returns>
		public override string ToString( JsonSerializationOptions options )
		{
			this.EnterSerialization( options );

			try
			{
				var sb = new StringBuilder();

				sb.Append( this.Indent( true ) + "{" );

				if( JsonConverter.SerializationContext.Options.Indented )
					sb.AppendLine();

				var first = true;

				foreach( var member in this.orderedMembers )
				{
					if( !first )
					{
						sb.Append( "," );
						if( JsonConverter.SerializationContext.Options.Indented )
							sb.AppendLine();
					}

					first = false;

					sb.Append( this.Indent() + ( ( JProperty )member ).ToString( options ) );
				}

				if( JsonConverter.SerializationContext.Options.Indented )
					sb.AppendLine();

				this.Outdent();

				sb.Append( this.Indent() + "}" );

				return sb.ToString();
			}
			finally
			{
				this.ExitSerialization();
			}
		}

		/// <summary>
		/// Gets the size, in bytes, of the BSON representation.
		/// </summary>
		/// <returns>The size, in bytes, of the BSON representation.</returns>
		public override int GetBsonSize()
		{
			var offset = 0;
			this.ToBson( null!, ref offset );
			return offset;
		}

		/// <summary>
		/// Gets the BSON size of the element with the specified name.
		/// </summary>
		/// <param name="ename">The element name.</param>
		/// <returns>The size of the element in bytes.</returns>
		public override int GetBsonSize( string ename ) => 1 + ename.Length + 1 + this.GetBsonSize();

		/// <summary>
		/// Serializes the object and its properties to BSON.
		/// </summary>
		/// <param name="buffer">The destination buffer, or <see langword="null"/> to calculate the serialized size without writing data.</param>
		/// <param name="offset">The offset in the buffer at which serialization begins; advanced past the serialized data.</param>
		public override void ToBson( byte[] buffer, ref int offset )
		{
			var startingOffset = offset;

			// leave space for the size
			offset += 4;

			foreach( var member in this.orderedMembers )
			{
				( ( JProperty )member ).ToBson( ( ( JProperty )member ).Name, buffer, ref offset );
			}

			// Write the trailing nul
			if( buffer != null )
				buffer[ offset ] = ( byte )0;
			++offset;

			// Write the completed size
			if( buffer != null )
				SerializationUtilities.Marshall( buffer, ref startingOffset, offset - startingOffset );
		}

		/// <summary>
		/// Gets the BSON type.
		/// </summary>
		/// <returns>The BSON document type.</returns>
		public override BsonTypes GetBsonType() => BsonTypes.BsonDocument;

		/// <summary>
		/// Deserializes a BSON document from a byte buffer into a JSON object.
		/// </summary>
		/// <param name="buffer">The buffer containing the BSON document.</param>
		/// <param name="offset">The zero-based position in <paramref name="buffer"/> at which reading begins. Updated to the position after the
		/// document.</param>
		/// <param name="factory">The factory used when deserializing nested values.</param>
		/// <returns>The deserialized JSON object.</returns>
		/// <exception cref="Exception">The document is malformed, such as when an element name terminator or trailing document terminator is missing.</exception>
		internal static JObject FromBson( byte[] buffer, ref int offset, InstanceFactory factory = null! )
		{
			var result = new JObject();

			var startingOffset = offset;
			var len = ( int )SerializationUtilities.Unmarshall( buffer, ref offset, TypeCode.Int32 );

			while( offset < startingOffset + len - 1 )
			{
				// get the element type
				var bsonType = ( BsonTypes )buffer[ offset++ ];
				// get the element name
				var idxNul = JToken.FindNul( buffer, offset );

				if( idxNul == -1 )
					throw new Exception( "Missing ename terminator" );

				var ename = JToken.ConvertToString( buffer, offset, idxNul - offset );
				offset = idxNul + 1;

				JToken item = null!;

				switch( bsonType )
				{
					case BsonTypes.BsonArray:
						item = JArray.FromBson( buffer, ref offset, factory );
						break;
					case BsonTypes.BsonDocument:
						item = JObject.FromBson( buffer, ref offset, factory );
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
						item = JValue.FromBson( bsonType, buffer, ref offset );
						break;
				}
				result.Add( ename, item! );
			}

			if( buffer[ offset++ ] != 0 )
				throw new Exception( "bad format - missing trailing null on bson document" );

			return result;
		}

		/// <summary>
		/// Adds a member or updates its existing property while preserving its position in the ordered members.
		/// </summary>
		/// <param name="name">The name of the member to add or update.</param>
		/// <param name="prop">The property to associate with the member.</param>
		private void AddOrUpdateMember( string name, JProperty prop )
		{
			if( this.members.Contains( name ) )
			{
				var old = this.members[ name ];
				var idx = this.orderedMembers.IndexOf( old );

				if( idx != -1 )
				{
					this.orderedMembers[ idx ] = prop;
				}
				else
				{
					this.orderedMembers.Add( prop );
				}

				this.members[ name ] = prop;
			}
			else
			{
				this.orderedMembers.Add( prop );
				this.members[ name ] = prop;
			}
		}
	}
}
