using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Diagnostics;

namespace GHIElectronics.TinyCLR.Data.Json
{
	// The protocol mantra: Be strict in what you emit, and generous in what you accept. ;-)

	/// <summary>
	/// Hook invoked during deserialization to create an object or array. Return null to use
	/// the default construction behavior. For arrays, baseType is the element type and
	/// length is non-negative; for objects, length is -1.
	/// </summary>
	public delegate object InstanceFactory( string instancePath, JToken token, Type baseType, string fieldName, int length );

	/// <summary>
	/// Parses JSON text into a <see cref="JToken"/> tree and serializes objects back
	/// to JSON. Strict-on-emit, generous-on-accept per RFC 8259.
	/// </summary>
	public static class JsonConverter
	{
		/// <summary>
		/// Specifies the kinds of tokens recognized by the JSON converter.
		/// </summary>
		private enum TokenType
		{
			LBrace, RBrace, LArray, RArray, Colon, Comma, String, Number, Date, Error,
			True, False, Null, End
		}

		/// <summary>
		/// Represents a lexical token.
		/// </summary>
		private struct LexToken
		{
			public TokenType tType;
			public string tValue;
		}

		/// <summary>
		/// Holds the formatting options and indentation state for a serialization pass.
		/// </summary>
		/// <param name="options">The formatting options to use for the serialization pass.</param>
		public class SerializationCtx( JsonSerializationOptions options )
		{
			/// <summary>
			/// The formatting options in effect for this serialization pass.
			/// </summary>
			public readonly JsonSerializationOptions Options = options;

			/// <summary>
			/// The current indentation nesting level.
			/// </summary>
			public int indentLevel;
		}

		private static readonly bool EnableDebugOutput = false;

		/// <summary>
		/// The serialization context shared by the current serialization pass.
		/// </summary>
		public static SerializationCtx SerializationContext = null!;

		/// <summary>
		/// Lock object used to synchronize access to the shared serialization context.
		/// </summary>
		public static object SyncObj = new();

		/// <summary>
		/// Serializes an object to a JSON token. IPAddress objects include an AddressBytes array
		/// containing the bytes returned by GetAddressBytes.
		/// </summary>
		/// <param name="oSource">The non-null object to serialize.</param>
		/// <param name="settings">The settings to use during serialization, or null to use default settings.</param>
		/// <returns>A <see cref="JArray"/> if <paramref name="oSource"/> is an array; otherwise, a <see cref="JObject"/>.</returns>
		public static JToken Serialize( object oSource, JsonSerializerSettings settings = null! )
		{
			settings ??= new JsonSerializerSettings();

			var type = oSource.GetType();

			return type switch
			{
				_ when type.IsArray => JArray.Serialize( type, oSource, settings ),
				_ when !type.IsArray => JObject.Serialize( type, oSource, settings ),
				_ => null
			};
		}

		/// <summary>
		/// Adds special serialization for IP addresses.
		/// </summary>
		/// <remarks>When <paramref name="source"/> is an <see cref="System.Net.IPAddress"/>, its address bytes are
		/// added to <paramref name="result"/> under the "AddressBytes" property.</remarks>
		/// <param name="source">The object to inspect for special members.</param>
		/// <param name="result">The JSON object to which serialized members are added.</param>
		/// <param name="settings">The settings used to serialize the address bytes.</param>
		internal static void SerializeSpecialMembers( object source, JObject result, JsonSerializerSettings settings )
		{
			if( source is IPAddress address )
			{
				var bytes = address.GetAddressBytes();
				result.Add( "AddressBytes", JArray.Serialize( typeof( byte[] ), bytes, settings ) );
			}
		}

		/// <summary>
		/// Deserializes JSON into an object of the specified type.
		/// </summary>
		/// <remarks>Objects and arrays are constructed automatically where their types are known.
		/// A factory can override construction or handle types without a default constructor.
		/// Type metadata can instantiate compatible types, so only deserialize trusted input.</remarks>
		/// <param name="sourceString">The JSON string to deserialize.</param>
		/// <param name="type">The type of object to create.</param>
		/// <param name="factory">The optional factory used to create instances during population.</param>
		/// <returns>The populated object.</returns>
		public static object DeserializeObject( string sourceString, Type type, InstanceFactory factory = null! )
		{
			var dserResult = Deserialize( sourceString );

			return PopulateObject( dserResult, type, "/", factory );
		}

		/// <summary>Deserializes JSON into an instance of the specified type.</summary>
		/// <remarks>TinyCLR may erase generic type arguments. Use the overload accepting an explicit Type on TinyCLR.</remarks>
		/// <param name="sourceString">The JSON string to deserialize.</param>
		/// <param name="factory">An optional factory for custom instances.</param>
		/// <returns>The populated object.</returns>
		[Obsolete( "On TinyCLR, use DeserializeObject(source, typeof(YourType)) instead; generic type arguments may be erased." )]
		public static T DeserializeObject<T>( string sourceString, InstanceFactory factory = null! ) =>
			( T )DeserializeObject( sourceString, GetGenericTargetType( typeof( T ) ), factory );

		/// <summary>Deserializes JSON from a stream into an instance of the specified type.</summary>
		/// <remarks>TinyCLR may erase generic type arguments. Use the overload accepting an explicit Type on TinyCLR.</remarks>
		/// <param name="stream">The stream containing JSON.</param>
		/// <param name="factory">An optional factory for custom instances.</param>
		/// <returns>The populated object.</returns>
		[Obsolete( "On TinyCLR, use DeserializeObject(source, typeof(YourType)) instead; generic type arguments may be erased." )]
		public static T DeserializeObject<T>( Stream stream, InstanceFactory factory = null! ) =>
			( T )DeserializeObject( stream, GetGenericTargetType( typeof( T ) ), factory );

		/// <summary>Deserializes JSON from a reader into an instance of the specified type.</summary>
		/// <remarks>TinyCLR may erase generic type arguments. Use the overload accepting an explicit Type on TinyCLR.</remarks>
		/// <param name="reader">The reader containing JSON.</param>
		/// <param name="factory">An optional factory for custom instances.</param>
		/// <returns>The populated object.</returns>
		[Obsolete( "On TinyCLR, use DeserializeObject(source, typeof(YourType)) instead; generic type arguments may be erased." )]
		public static T DeserializeObject<T>( StreamReader reader, InstanceFactory factory = null! ) =>
			( T )DeserializeObject( reader, GetGenericTargetType( typeof( T ) ), factory );

		/// <summary>
		/// Gets the generic target type.
		/// </summary>
		/// <param name="type">The target type.</param>
		/// <returns>The generic target type.</returns>
		/// <exception cref="NotSupportedException">The type is <see cref="object"/>, which may indicate that the generic target type was erased.</exception>
		private static Type GetGenericTargetType( Type type )
		{
			if( type == typeof( object ) )
				throw new NotSupportedException( "The generic target type may have been erased. Use the overload with an explicit Type." );
			return type;
		}

		/// <summary>
		/// Deserializes an object from a stream.
		/// </summary>
		/// <param name="stream">The stream containing the serialized object.</param>
		/// <param name="type">The type of object to deserialize.</param>
		/// <param name="factory">The factory used to create object instances.</param>
		/// <returns>The deserialized object.</returns>
		public static object DeserializeObject( Stream stream, Type type, InstanceFactory factory = null! )
		{
			var dserResult = Deserialize( stream );
			return PopulateObject( dserResult, type, "/", factory );
		}

		/// <summary>
		/// Deserializes an object from the specified reader into the specified type.
		/// </summary>
		/// <param name="sr">The reader containing the serialized object.</param>
		/// <param name="type">The type of object to deserialize.</param>
		/// <param name="factory">The factory used to create object instances, or null to use the default behavior.</param>
		/// <returns>The deserialized object.</returns>
		public static object DeserializeObject( StreamReader sr, Type type, InstanceFactory factory = null! )
		{
			var dserResult = Deserialize( sr );
			return PopulateObject( dserResult, type, "/", factory );
		}

		/// <summary>
		/// Creates an instance using type metadata in the JSON object or the specified base type.
		/// </summary>
		/// <param name="instancePath">The path of the instance being created.</param>
		/// <param name="token">The JSON token that may contain type metadata.</param>
		/// <param name="baseType">The fallback type to instantiate when the token does not specify a type.</param>
		/// <param name="fieldName">The name of the field associated with the instance.</param>
		/// <param name="length">The array length, or -1 when creating a non-array instance.</param>
		/// <returns>The created instance, or null when no type is available.</returns>
		private static object DefaultInstanceFactory( string instancePath, JToken token, Type baseType, string fieldName, int length )
		{
			object instance = null!;

			if( length != -1 )
				return baseType == null ? null! : Array.CreateInstance( baseType, length );

			if( baseType == typeof( object ) && token is JObject untyped && !untyped.Contains( "$type" ) )
				throw new NotSupportedException( "Cannot deserialize an object without a concrete type or $type metadata at " + instancePath );

			if( token is JObject jobj && jobj.Contains( "$type" ) )
			{
				var metadata = jobj[ "$type" ].Value as JValue;
				var typeDefn = metadata == null ? null : metadata.Value as string;

				if( string.IsNullOrEmpty( typeDefn ) )
					throw new Exception( "Invalid $type metadata at " + instancePath );

				var resolvedType = Type.GetType( typeDefn );
				if( resolvedType == null || ( baseType != null && resolvedType != baseType && !resolvedType.IsSubclassOf( baseType ) && !IsImplementedInterface( resolvedType, baseType ) ) )
					throw new Exception( "Invalid or incompatible $type at " + instancePath + ": " + typeDefn );

				baseType = resolvedType;
			}

			if( baseType != null )
				instance = AppDomain.CurrentDomain.CreateInstanceAndUnwrap( baseType.Assembly.FullName, baseType.FullName );

			return instance;
		}

		/// <summary>
		/// Determines whether a type implements a specified interface.
		/// </summary>
		/// <param name="type">The type to inspect.</param>
		/// <param name="baseType">The interface type to find.</param>
		/// <returns>true if the type implements the specified interface; otherwise, false.</returns>
		private static bool IsImplementedInterface( Type type, Type baseType )
		{
			foreach( var implemented in type.GetInterfaces() )
				if( implemented == baseType )
					return true;

			return false;
		}

		/// <summary>
		/// Populates an object or array from a JSON token.
		/// </summary>
		/// <remarks>Object member names are matched case-insensitively.</remarks>
		/// <param name="root">The JSON token containing the data to populate.</param>
		/// <param name="type">The type of object to create when the token is an object.</param>
		/// <param name="path">The path identifying the token in the JSON document.</param>
		/// <param name="factory">The factory used to create target instances and arrays.</param>
		/// <returns>The populated value.</returns>
		/// <exception cref="Exception">A value cannot be constructed or is incompatible with its target type.</exception>
		private static object PopulateObject( JToken root, Type type, string path, InstanceFactory factory, string fieldName = null! )
		{
			if( root is JValue value )
				return ConvertScalar( value, type );

			if( root is JArray jarray )
			{
				var elementType = type != null && type.IsArray ? type.GetElementType() : null!;
				var supplied = factory == null ? null : factory( path, root, elementType, fieldName, jarray.Length );
				var array = supplied == null ? ( Array )DefaultInstanceFactory( path, root, elementType, fieldName, jarray.Length ) : supplied as Array;

				if( array == null || array.Length != jarray.Length || ( type != null && !type.IsInstanceOfType( array ) ) )
					throw new Exception( "Cannot create a compatible array at " + path );

				elementType = array.GetType().GetElementType();
				var values = new ArrayList();

				for( var i = 0; i < jarray.Length; i++ )
					values.Add( PopulateObject( jarray[ i ], elementType, path + "[" + i + "]", factory ) );

				values.CopyTo( array );

				return array;
			}

			if( root is JObject jobj )
			{
				var instance = factory == null ? null : factory( path, root, type, null!, -1 );
				if( instance != null && type != null && !type.IsInstanceOfType( instance ) )
					throw new Exception( "Factory returned an incompatible instance at " + path );

				if( instance == null && type == typeof( IPAddress ) )
				{
					if( !jobj.Contains( "AddressBytes" ) || !( jobj[ "AddressBytes" ].Value is JArray addressBytes ) || addressBytes.Length != 4 )
						throw new Exception( "Invalid IP address at " + path );
					return new IPAddress( ( byte[] )PopulateObject( addressBytes, typeof( byte[] ), path + "/AddressBytes", factory ) );
				}

				instance ??= DefaultInstanceFactory( path, root, type, null!, -1 );
				if( instance == null )
					throw new Exception( "Failed to create target instance at " + path );

				var resolvedType = instance.GetType();
				const BindingFlags flags = BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public;
				foreach( var item in jobj.Members )
				{
					var prop = ( JProperty )item;
					var field = resolvedType.GetField( prop.Name, flags );
					MethodInfo setter = null!;
					Type memberType;
					if( field != null )
						memberType = field.FieldType;
					else
					{
						var getter = resolvedType.GetMethod( "get_" + prop.Name, flags );
						if( getter == null )
							continue;
						setter = resolvedType.GetMethod( "set_" + prop.Name, flags | BindingFlags.NonPublic );
						if( setter == null )
							continue;
						memberType = getter.ReturnType;
					}

					var childPath = path[ path.Length - 1 ] == '/' ? path + prop.Name : path + "/" + prop.Name;
					var child = prop.Value is JArray
						? PopulateObject( prop.Value, memberType, path, factory, prop.Name )
						: PopulateObject( prop.Value, memberType, childPath, factory );
					if( field != null )
						field.SetValue( instance, child );
					else
						setter.Invoke( instance, new object[] { child } );
				}
				return instance;
			}
			throw new Exception( "Unsupported JSON token at " + path );
		}

		/// <summary>
		/// Converts a scalar JSON value to the specified type.
		/// </summary>
		/// <param name="value">The scalar JSON value to convert.</param>
		/// <param name="targetType">The type to convert the scalar value to.</param>
		/// <returns>The converted value, or null if the source value is null.</returns>
		/// <exception cref="Exception">The source value's type cannot be converted to an enum value.</exception>
		private static object ConvertScalar( JValue value, Type targetType )
		{
			object result = null!;

			// This is a matrix of conversions from the source (value) type to targetType.
			// This is a bit long because the available Convert.ToXXXX functions are more
			// limited than in .net, so we have to do the scalar conversions ourselves.

			//result = (value, targetType) switch
			//{
			//	_ when value.Value == null => null!,
			//	_ when targetType == value.Value.GetType() => value.Value,
			//	_ when targetType == typeof( DateTime ) => DateTimeExtensions.FromIso8601( value.Value.ToString() ),
			//	_ => throw new Exception( "Unsupported conversion" )
			//};

			if( value.Value == null )
			{
				if( targetType != null && targetType.IsValueType )
					throw new Exception( "Cannot assign null to " + targetType.FullName );
				result = null!;
			}
			else if( targetType == null || targetType == typeof( object ) || targetType == value.Value.GetType() )
			{
				result = value.Value;
			}
			else if( targetType == typeof( char ) && value.Value is string text && text.Length == 1 )
			{
				result = text[ 0 ];
			}
			else if( value.Value is int intValue && targetType != typeof( int ) )
			{
				return ConvertScalar( new JValue( ( long )intValue ), targetType );
			}
			else if( targetType == typeof( DateTime ) )
			{
				DateTime dt;
				var sdt = value.Value.ToString();
				if( sdt.Contains( "Date(" ) )
					dt = DateTimeExtensions.FromASPNetAjax( sdt );
				else
					dt = DateTimeExtensions.FromIso8601( sdt );

				result = dt;
			}
			else
			{
				if( targetType.IsEnum )
				{
					if( value.Value is string enumName )
					{
						var enumField = targetType.GetField( enumName, BindingFlags.Public | BindingFlags.Static );
						if( enumField != null )
							return enumField.GetValue( null! );
						if( long.TryParse( enumName, out var signedValue ) )
							return ConvertScalar( new JValue( signedValue ), targetType );
						if( ulong.TryParse( enumName, out var unsignedValue ) )
							return ConvertScalar( new JValue( unsignedValue ), targetType );
						throw new Exception( "Unknown enum value: " + enumName );
					}

					// This is broken in TinyCLR
					var baseType = Enum.GetUnderlyingType( targetType );

					// explicit unboxing, because unboxing with casting fails
					var source = value.Value;
					var sourceType = value.Value.GetType();

					ulong ulSource = sourceType switch
					{
						_ when sourceType == typeof( ulong ) => ( ulong )source,
						_ when sourceType == typeof( int ) => ( ulong )( int )source,
						_ when sourceType == typeof( long ) => ( ulong )( long )source,
						_ when sourceType == typeof( short ) => ( ulong )( ushort )source,
						_ when sourceType == typeof( byte ) => ( ulong )( byte )source,
						_ when sourceType == typeof( sbyte ) => ( ulong )( sbyte )source,
						_ => throw new Exception( "Could not encode the source type for use in an enum" )
					};
				
					result = baseType switch
					{
						_ when baseType == typeof( int ) => ( int )ulSource,
						_ when baseType == typeof( long ) => ( long )ulSource,
						_ when baseType == typeof( ulong ) => ulSource,
						_ when baseType == typeof( short ) => ( short )ulSource,
						_ when baseType == typeof( ushort ) => ( ushort )ulSource,
						_ when baseType == typeof( byte ) => ( byte )ulSource,
						_ when baseType == typeof( sbyte ) => ( sbyte )ulSource,
						_ => throw new Exception( "Could not encode the base type for use in an enum" )
					};
				}
				else if( value.Value.GetType() == typeof( ulong ) )
				{
					var source = ( ulong )value.Value;

					result = targetType switch
					{
						_ when targetType == typeof( float ) => ( float )source,
						_ when targetType == typeof( double ) => ( double )source,
						_ when targetType == typeof( long ) => ( long )source,
						_ when targetType == typeof( short ) => ( short )source,
						_ when targetType == typeof( ushort ) => ( ushort )source,
						_ when targetType == typeof( byte ) => checked( ( byte )source ),
						_ when targetType == typeof( sbyte ) => checked( ( sbyte )source ),
						_ when targetType == typeof( int ) => ( int )source,
						_ when targetType == typeof( uint ) => ( uint )source,
						_ => value.Value
					};

				}
				else if( value.Value.GetType() == typeof( long ) )
				{
					var source = ( long )value.Value;

					result = targetType switch
					{
						_ when targetType == typeof( float ) => ( float )source,
						_ when targetType == typeof( double ) => ( double )source,
						_ when targetType == typeof( ulong ) => ( ulong )source,
						_ when targetType == typeof( short ) => ( short )source,
						_ when targetType == typeof( ushort ) => ( ushort )source,
						_ when targetType == typeof( byte ) => checked( ( byte )source ),
						_ when targetType == typeof( sbyte ) => checked( ( sbyte )source ),
						_ when targetType == typeof( int ) => ( int )source,
						_ when targetType == typeof( uint ) => ( uint )source,
						_ => value.Value
					};
				}
				else if( value.Value.GetType() == typeof( double ) )
				{
					var source = ( double )value.Value;

					result = targetType switch
					{
						_ when targetType == typeof( float ) => ( float )source,
						_ when targetType == typeof( long ) => ( long )source,
						_ when targetType == typeof( ulong ) => ( ulong )source,
						_ when targetType == typeof( short ) => ( short )source,
						_ when targetType == typeof( ushort ) => ( ushort )source,
						_ when targetType == typeof( int ) => ( int )source,
						_ when targetType == typeof( uint ) => ( uint )source,
						_ => value.Value
					};
				}
			}

			if( value.Value != null && targetType != null && !targetType.IsEnum && ( result == null || !targetType.IsInstanceOfType( result ) ) )
				throw new Exception( "Cannot convert scalar value to " + targetType.FullName );
			return result!;
		}

		/// <summary>
		/// Converts a property's scalar value to the specified type.
		/// </summary>
		/// <param name="prop">The property whose value is converted.</param>
		/// <param name="targetType">The type to convert the scalar value to.</param>
		/// <returns>The converted scalar value.</returns>
		private static object ConvertScalar( JProperty prop, Type targetType ) => ConvertScalar( ( JValue )prop.Value, targetType );

		/// <summary>
		/// Deserializes a JSON string into a token.
		/// </summary>
		/// <param name="sourceString">The JSON string to deserialize.</param>
		/// <returns>The token representing the deserialized JSON value.</returns>
		public static JToken Deserialize( string sourceString )
		{
			var data = Encoding.UTF8.GetBytes( sourceString );
			var mem = new MemoryStream( data );

			mem.Seek( 0, SeekOrigin.Begin );

			return Deserialize( new StreamReader( mem ) );
		}


		/// <summary>
		/// Deserializes a JSON stream into a token.
		/// </summary>
		/// <param name="sourceStream">The stream containing the JSON data to deserialize.</param>
		/// <returns>The token representing the deserialized JSON value.</returns>
		public static JToken Deserialize( Stream sourceStream ) => Deserialize( new StreamReader( sourceStream ) );

		/// <summary>
		/// Deserializes JSON from a reader into a token.
		/// </summary>
		/// <param name="sourceReader">The reader containing the JSON data to deserialize.</param>
		/// <returns>The token representing the deserialized JSON value.</returns>
		public static JToken Deserialize( StreamReader sourceReader )
		{
			LexToken token = GetNextToken( sourceReader );
			
			JToken result;

			switch( token.tType )
			{
				case TokenType.LBrace:
					result = ParseObject( sourceReader, ref token );

					if( token.tType == TokenType.RBrace )
						token = GetNextToken( sourceReader );
					else if( token.tType != TokenType.End && token.tType != TokenType.Error )
						throw new Exception( "unexpected content after end of object" );
					break;

				case TokenType.LArray:
					result = ParseArray( sourceReader, ref token );

					if( token.tType == TokenType.RArray )
						token = GetNextToken( sourceReader );
					else if( token.tType != TokenType.End && token.tType != TokenType.Error )
						throw new Exception( "unexpected content after end of array" );
					break;

				default:
					throw new Exception( "unexpected initial token in json parse" );
			}

			if( token.tType != TokenType.End )
				throw new Exception( "unexpected end token in json parse" );
			else if( token.tType == TokenType.Error )
				throw new Exception( "unexpected lexical token during json parse" );

			return result;
		}

		/// <summary>Decodes a BSON byte buffer into a JSON token tree.</summary>
		/// <param name="buffer">The BSON byte buffer to decode.</param>
		/// <param name="factory">An optional instance factory for creating objects.</param>
		/// <returns>The token representing the decoded JSON value.</returns>
		public static JToken FromBson( byte[] buffer, InstanceFactory factory = null! )
		{
			var offset = 0;
			var len = ( int )SerializationUtilities.Unmarshall( buffer, ref offset, TypeCode.Int32 );

			JToken dserResult = null!;

			while( offset < buffer.Length - 1 )
			{
				var bsonType = ( BsonTypes )buffer[ offset++ ];

				// eat the empty ename
				var idxNul = JToken.FindNul( buffer, offset );

				if( idxNul == -1 )
					throw new Exception( "Missing ename terminator" );

				var ename = JToken.ConvertToString( buffer, offset, idxNul - offset );

				offset = idxNul + 1;

				switch( bsonType )
				{
					case BsonTypes.BsonDocument:
						dserResult = JObject.FromBson( buffer, ref offset, factory );
						break;
					case BsonTypes.BsonArray:
						dserResult = JArray.FromBson( buffer, ref offset, factory );
						break;
					default:
						throw new Exception( "unexpected top-level object type in bson" );
				}
			}
			if( buffer[ offset++ ] != 0 )
				throw new Exception( "bad format - missing trailing null on bson document" );

			return dserResult;
		}

		/// <summary>Decodes a BSON byte buffer and populates a new instance of the given type.</summary>
		/// <param name="buffer">The BSON byte buffer to decode.</param>
		/// <param name="resultType">The type of the object to create and populate.</param>
		/// <param name="factory">An optional instance factory for creating objects.</param>
		/// <returns>The populated object.</returns>
		public static object FromBson( byte[] buffer, Type resultType, InstanceFactory factory = null! )
		{
			var jtoken = FromBson( buffer );

			return PopulateObject( jtoken, resultType, "/", factory );
		}

		/// <summary>
		/// Parses a JSON object from the input stream.
		/// </summary>
		/// <param name="sr">The reader containing the JSON input.</param>
		/// <param name="token">The current token; must represent the opening brace and is updated during parsing.</param>
		/// <returns>The parsed JSON object.</returns>
		/// <exception cref="Exception">The object contains an invalid token, has an unexpected label or missing colon, or is unterminated.</exception>
		private static JObject ParseObject( StreamReader sr, ref LexToken token )
		{
			Debug.Assert( token.tType == TokenType.LBrace );

			var result = new JObject();
			token = GetNextToken( sr );

			while( token.tType != TokenType.End && token.tType != TokenType.Error && token.tType != TokenType.RBrace )
			{
				if( token.tType != TokenType.String )
					throw new Exception( "expected label" );
				
				var propName = token.tValue;

				token = GetNextToken( sr );
				
				if( token.tType != TokenType.Colon )
					throw new Exception( "expected colon" );

				var value = ParseValue( sr, ref token );
				result.Add( propName, value );

				token = GetNextToken( sr );

				if( token.tType == TokenType.Comma )
					token = GetNextToken( sr );

			}
			
			if( token.tType == TokenType.Error )
				throw new Exception( "unexpected token in json object" );
			else if( token.tType != TokenType.RBrace )
				throw new Exception( "unterminated json object" );

			return result;
		}

		/// <summary>
		/// Parses a JSON array from the input stream.
		/// </summary>
		/// <param name="sr">The reader containing the JSON input.</param>
		/// <param name="token">The current token; must represent the opening bracket and is updated during parsing.</param>
		/// <returns>The parsed JSON array.</returns>
		/// <exception cref="Exception">The array contains an invalid token or is unterminated.</exception>
		private static JArray ParseArray( StreamReader sr, ref LexToken token )
		{
			Debug.Assert( token.tType == TokenType.LArray || token.tType == TokenType.RArray );

			var list = new ArrayList();

			while( token.tType != TokenType.End && token.tType != TokenType.Error && token.tType != TokenType.RArray )
			{
				var value = ParseValue( sr, ref token );
				if( value != null )
				{
					list.Add( value );
					token = GetNextToken( sr );
					if( token.tType != TokenType.Comma && token.tType != TokenType.RArray )
						throw new Exception( "badly formed array" );
				}
			}
			;
			if( token.tType == TokenType.Error )
				throw new Exception( "unexpected token in array" );
			else if( token.tType != TokenType.RArray )
				throw new Exception( "unterminated json array" );

			var result = new JArray( ( JToken[] )list.ToArray( typeof( JToken ) ) );

			return result;
		}
		
		/// <summary>
		/// Parses a JSON value from the input stream.
		/// </summary>
		/// <param name="sr">The reader containing the JSON input.</param>
		/// <param name="token">The current token; is updated during parsing.</param>
		/// <returns>The parsed JSON value.</returns>
		/// <exception cref="Exception">The value is invalid or unexpected.</exception>
		private static JToken ParseValue( StreamReader sr, ref LexToken token )
		{
			token = GetNextToken( sr );

			if( token.tType == TokenType.RArray )
			{
				// we were expecting a value in an array, and came across the end-of-array marker,
				//  so this is an empty array
				return null!;
			}
			else if( token.tType == TokenType.String )
				return new JValue( token.tValue );
			else if( token.tType == TokenType.Number )
			{
				// If it looks like a double, use double as the underlying storage type
				if( token.tValue.Length > 0 && ( token.tValue[ 0 ] == 'E' || token.tValue[ 0 ] == 'e' ) )
				{
					// exponent with no leading mantissa
					return new JValue( double.Parse( "1" + token.tValue ) );
				}
				else if( token.tValue.IndexOfAny( new char[] { '.', 'e', 'E' } ) != -1 )
				{
					// well-formed double, with or without E notation
					return new JValue( double.Parse( token.tValue ) );
				}
				else
				{
					// Otherwise, an int of some type
					// Use Int64 as the underlying storage type. If you don't see a minus
					// sign, then prefer UInt64 for maximum range.
					if( token.tValue.IndexOf( '-' ) != -1 )
						return new JValue( long.Parse( token.tValue ) );
					else
						return new JValue( ulong.Parse( token.tValue ) );
				}
			}
			else if( token.tType == TokenType.True )
				return new JValue( true );
			else if( token.tType == TokenType.False )
				return new JValue( false );
			else if( token.tType == TokenType.Null )
				return new JValue( null! );
			else if( token.tType == TokenType.Date )
			{
				throw new NotSupportedException( "datetime parsing not supported" );
			}
			else if( token.tType == TokenType.LBrace )
				return ParseObject( sr, ref token );
			else if( token.tType == TokenType.LArray )
				return ParseArray( sr, ref token );

			throw new Exception( "invalid value found during json parse" );
		}

		/// <summary>
		/// Retrieves the next token from the input stream.
		/// </summary>
		/// <param name="sourceReader">The reader containing the JSON input.</param>
		/// <returns>The next token from the input stream.</returns>
		private static LexToken GetNextToken( StreamReader sourceReader )
#if DEBUG
		{
			// a diagnostic wrapper, used only in debug builds
			var result = GetNextTokenInternal( sourceReader );

			Debug.WriteLineIf( EnableDebugOutput, result.tType.TokenTypeToString() );

			if( result.tType == TokenType.String || result.tType == TokenType.Number )
				Debug.WriteLineIf( EnableDebugOutput, "    " + result.tValue );

			return result;
		}

		/// <summary>
		/// Reads the next lexical token from the input.
		/// </summary>
		/// <remarks>Accepts single- and double-quoted strings. Number tokens are not validated for numeric
		/// well-formedness.</remarks>
		/// <param name="sourceReader">The reader containing the input to tokenize.</param>
		/// <returns>The next token parsed from the input.</returns>
		/// <exception cref="Exception">The input contains an unsupported escape sequence, an invalid Unicode escape digit, an unexpected character, or a
		/// malformed literal.</exception>
		private static LexToken GetNextTokenInternal( StreamReader sourceReader )
#endif
		{
			StringBuilder sb = null!;
			var openQuote = '\0';
			while( true ) // EndOfStream doesn't seem to work for mem streams - it's always 'true'
			{
				char ch = ( char )sourceReader.Read();

				// Handle json escapes per RFC 8259. Note: \' is non-standard but
				// we accept it for backward compat with the original port.
				var escaped = false;
				if( ch == '\\' )
				{
					escaped = true;
					ch = ( char )sourceReader.Read();

					if( ch == ( char )0xffff )
						return EndToken( sb! );

					switch( ch )
					{
						case '\'': ch = '\''; break;
						case '"': ch = '"'; break;
						case '\\': ch = '\\'; break;
						case '/': ch = '/'; break;
						case 'b': ch = '\b'; break;
						case 'f': ch = '\f'; break;
						case 't': ch = '\t'; break;
						case 'r': ch = '\r'; break;
						case 'n': ch = '\n'; break;
						case 'u':
							// \uXXXX - 4 hex digits, big-endian, BMP code point.
							// We don't currently decode surrogate pairs (😀);
							// each \uXXXX is emitted as one char and the caller's UTF-16
							// surrogate is preserved if both halves are present.
							var code = 0;

							for( var i = 0; i < 4; i++ )
							{
								var hex = ( char )sourceReader.Read();
								if( hex == ( char )0xffff )
									return EndToken( sb! );
								int d;
								if( hex >= '0' && hex <= '9' ) d = hex - '0';
								else if( hex >= 'a' && hex <= 'f' ) d = 10 + ( hex - 'a' );
								else if( hex >= 'A' && hex <= 'F' ) d = 10 + ( hex - 'A' );
								else throw new Exception( "invalid \\u hex digit" );
								code = ( code << 4 ) | d;
							}
							ch = ( char )code;
							break;
						default:
							throw new Exception( "unsupported escape" );
					}
				}

				if( sb != null && ( ch != openQuote || escaped ) )
				{
					sb.Append( ch );
				}
				else if( IsNumberIntroChar( ch ) )
				{
					sb = new StringBuilder();

					while( IsNumberChar( ch ) )
					{
						sb.Append( ch );
						// Don't consume chars that are not part of the number
						ch = ( char )sourceReader.Peek();

						if( IsNumberChar( ch ) )
							sourceReader.Read();

						if( ch == ( char )0xffff )
							return EndToken( sb );
					}
					// Note that we don't claim that this is a well-formed number
					return new LexToken() { tType = TokenType.Number, tValue = sb.ToString() };
				}
				else
				{
					//return ch switch
					//{
					//	'{' => new LexToken() { tType = TokenType.LBrace, tValue = null! },
					//	'}' => new LexToken() { tType = TokenType.RBrace, tValue = null! },
					//	'[' => new LexToken() { tType = TokenType.LArray, tValue = null! },
					//	']' => new LexToken() { tType = TokenType.RArray, tValue = null! },
					//	':' => new LexToken() { tType = TokenType.Colon, tValue = null! },
					//	',' => new LexToken() { tType = TokenType.Comma, tValue = null! },
					//	'"' or '\'' => HandleQuote( ref sb, ref openQuote, ch ),
					//	' ' or '\t' or '\r' or '\n' => GetNextToken( sourceReader ), // whitespace - go around again
					//	( char )0xffff => EndToken( sb! ),
					//	_ => HandleLiteral( sourceReader, ch )
					//};

					switch( ch )
					{
						case '{':
							return new LexToken() { tType = TokenType.LBrace, tValue = null! };
						case '}':
							return new LexToken() { tType = TokenType.RBrace, tValue = null! };
						case '[':
							return new LexToken() { tType = TokenType.LArray, tValue = null! };
						case ']':
							return new LexToken() { tType = TokenType.RArray, tValue = null! };
						case ':':
							return new LexToken() { tType = TokenType.Colon, tValue = null! };
						case ',':
							return new LexToken() { tType = TokenType.Comma, tValue = null! };
						case '"':
						case '\'':
							if( sb == null )
							{
								openQuote = ch;
								sb = new StringBuilder();
							}
							else
							{
								// We're building a string and we hit a quote character.
								// The ch must match openQuote, or otherwise we should have eaten it above as string content
								Debug.Assert( ch == openQuote );
								return new LexToken() { tType = TokenType.String, tValue = sb.ToString() };
							}
							break;
						case ' ':
						case '\t':
						case '\r':
						case '\n':
							break; // whitespace - go around again
						case ( char )0xffff:
							return EndToken( sb! );
						default:
							// try to collect a token
							switch( char.ToLower( ch ) )
							{
								case 't':
									Expect( sourceReader, 'r' );
									Expect( sourceReader, 'u' );
									Expect( sourceReader, 'e' );
									return new LexToken() { tType = TokenType.True, tValue = null! };
								case 'f':
									Expect( sourceReader, 'a' );
									Expect( sourceReader, 'l' );
									Expect( sourceReader, 's' );
									Expect( sourceReader, 'e' );
									return new LexToken() { tType = TokenType.False, tValue = null! };
								case 'n':
									Expect( sourceReader, 'u' );
									Expect( sourceReader, 'l' );
									Expect( sourceReader, 'l' );
									return new LexToken() { tType = TokenType.Null, tValue = null! };
								default:
									throw new Exception( "unexpected character during json lexical parse" );
							}
					}
				}
			}
		}

		/// <summary>
		/// Checks whether the next character in the stream matches the expected character.
		/// </summary>
		/// <param name="sr">The reader from which to read the next character.</param>
		/// <param name="ch">The expected character, compared without regard to the case of the character read.</param>
		/// <exception cref="Exception">The next character does not match <paramref name="ch"/>.</exception>
		private static void Expect( StreamReader sr, char ch )
		{
			if( char.ToLower( ( char )sr.Read() ) != ch )
				throw new Exception( "unexpected character during json lexical parse" );
		}

		/// <summary>
		/// Determines whether a character is an ASCII letter or digit.
		/// </summary>
		/// <param name="ch">The character to check.</param>
		/// <returns>`true` if the character is an ASCII letter or digit; otherwise, `false`.</returns>
		private static bool IsValidTokenChar( char ch ) => ( ch >= 'a' && ch <= 'z' ) ||
				   ( ch >= 'A' && ch <= 'Z' ) ||
				   ( ch >= '0' && ch <= '9' );

		/// <summary>
		/// Creates an error token when a token buffer is present; otherwise, creates an end-of-input token.
		/// </summary>
		/// <param name="sb">The token buffer, or null when no token remains.</param>
		/// <returns>An error token if sb is not null; otherwise, an end-of-input token.</returns>
		private static LexToken EndToken( StringBuilder sb )
		{
			if( sb != null )
				return new LexToken() { tType = TokenType.Error, tValue = null! };
			else
				return new LexToken() { tType = TokenType.End, tValue = null! };
		}

		/// <summary>
		/// Indicates whether a character is recognized as part of a number.
		/// </summary>
		/// <param name="ch">The character to check.</param>
		/// <returns>true if the character is a sign, decimal point, digit, or exponent marker; otherwise, false.</returns>
		private static bool IsNumberIntroChar( char ch ) => ( ch == '-' ) || ( ch == '+' ) || ( ch == '.' ) || ( ch >= '0' & ch <= '9' ) || ( ch == 'e' ) || ( ch == 'E' );  // 'e' for exponential notation

		/// <summary>
		/// Indicates whether a character is recognized as part of a number (excluding the first character).
		/// </summary>
		/// <param name="ch">The character to check.</param>
		/// <returns>true if the character is a sign, decimal point, digit, or exponent marker; otherwise, false.</returns>
		private static bool IsNumberChar( char ch ) => ( ch == '-' ) || ( ch == '+' ) || ( ch == '.' ) || ( ch == 'e' ) || ( ch == 'E' ) || ( ch >= '0' & ch <= '9' );

		/// <summary>
		/// Converts a token type to its string representation.
		/// </summary>
		/// <param name="tokenType">The token type to convert.</param>
		/// <returns>The token type's string representation, or "??unknown??" if the value is not recognized.</returns>
#if DEBUG
		private static string TokenTypeToString( this TokenType tokenType )
		{
			return tokenType switch
			{
				TokenType.Colon		=> "COLON",
				TokenType.Comma		=> "COMMA",
				TokenType.Date		=> "DATE",
				TokenType.End		=> "END",
				TokenType.Error		=> "ERROR",
				TokenType.LArray	=> "LARRAY",
				TokenType.LBrace	=> "LBRACE",
				TokenType.Number	=> "NUMBER",
				TokenType.RArray	=> "RARRAY",
				TokenType.RBrace	=> "RBRACE",
				TokenType.String	=> "STRING",
				TokenType.Null		=> "NULL",
				TokenType.True		=> "TRUE",
				TokenType.False		=> "FALSE",
				_					=> "??unknown??"
			};
		}
#endif
	}
}
