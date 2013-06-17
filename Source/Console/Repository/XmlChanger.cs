using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml.XPath;
using Onyx.XPatch.Console.xml;
using Attribute = Onyx.XPatch.Console.xml.Attribute;

namespace Onyx.XPatch.Console.Repository
{
    public static class XmlChanger
    {
        public static void ProcessDocument(XDocument xmlDocument, IEnumerable<Element> elements)
        {
            ProcessDocument(xmlDocument.Root, elements);
        }

        private static void ProcessDocument(XNode rootNode, IEnumerable<Element> elements)
        {
            if (elements == null) return;

            foreach (var element in elements)
            {
                var selectedNodes = 
                    rootNode
                        .XPathSelectElements(element.XPath)
                        .OrderBy(element.ProcessingOrder);

                switch (element.Action)
                {
                    case ElementAction.Remove:
                        foreach (var node in selectedNodes.ToArray())
                        {
                            node.Remove();
                        }
                        break;
                    case ElementAction.Rename:
                        foreach (var node in selectedNodes.ToArray())
                        {
                            var newName = EvaluateValue(node, element.Name);
                            var newNode = RenameNode(node, string.Empty, newName);

                            if (node == rootNode)
                            {
                                rootNode = newNode;
                            }
                        }
                        break;
                    case ElementAction.Add:
                        foreach (var node in selectedNodes)
                        {
                            AddElement(element, node);
                        }
                        break;
                    case ElementAction.Update:
                        foreach (var node in selectedNodes)
                        {
                            if (!string.IsNullOrEmpty(element.Value))
                            {
                                var value = EvaluateValue(node, element.Value);
                                
                                if (element.UseCData)
                                {
                                    var cdataSection = new XCData(value);

                                    node.ReplaceAll(cdataSection);
                                }
                                else
                                {
                                    node.SetValue(value);
                                }
                            }

                            ProcessAttributes(node, element.Attributes);
                            ProcessDocument(node, element.Elements);
                        }
                        break;
                    case ElementAction.Copy:
                        var nodes = selectedNodes.ToArray();
                        foreach (var node in nodes)
                        {
                            var parentNode = node.Parent;
                            var targetNode = parentNode.XPathSelectElement(element.TargetXPath);

                            if (element.Order <= 0)
                            {
                                targetNode.AddFirst(node);
                            }
                            else
                            {
                                targetNode.Elements().ElementAt(element.Order).AddBeforeSelf(node);
                            }
                        }
                        break;
                    case ElementAction.Move:
                        foreach (var node in selectedNodes.ToArray())
                        {
                            var parentNode = node.Parent;
                            var targetNode = parentNode.XPathSelectElement(element.TargetXPath);

                            node.Remove();

                            if (element.Order <= 0)
                            {
                                targetNode.AddFirst(node);
                            }
                            else
                            {
                                targetNode.Elements().ElementAt(element.Order).AddBeforeSelf(node);
                            }
                        }
                        break;
                    case ElementAction.MoveAfter:
                        foreach (var node in selectedNodes.ToArray())
                        {
                            var parentNode = node.Parent;
                            var targetNode = parentNode.XPathSelectElement(element.TargetXPath);

                            node.Remove();

                            targetNode.AddAfterSelf(node);
                        }
                        break;
                    case ElementAction.DocumentUpdate:
                        foreach (var node in selectedNodes)
                        {
                            // Fix input documents inside test document
                            var doc = XDocument.Parse(node.Value);

                            node.ReplaceAll(doc.Root);

                            ProcessDocument(node, element.Elements);

                            doc.ReplaceNodes(node.Nodes());

                            var innerXml = PrettyPrint(doc);
                            var cdataSection = new XCData(innerXml);
                            
                            node.ReplaceAll(cdataSection);
                        }
                        break;
                    case ElementAction.ParseSqlInsert:
                        foreach (var node in selectedNodes)
                        {
                            // fix sql insert inside
                            var parsedXml = ParseSqlInsert(node.Value);

                            node.ReplaceAll(parsedXml);

                            ProcessDocument(node, element.Elements);

                            var sqlInsert = PrintSqlInsert(node.Elements().First());
                            var cdataSection = new XCData(sqlInsert);

                            node.ReplaceAll(cdataSection);
                        }
                        break;
                    case ElementAction.ValidateSchema:
                        foreach (var node in selectedNodes)
                        {
                            //TODO: use Validate method
                            if (!ValidateAgainstSchema(node.ToString(), node.Name.LocalName, element.ComplexType, element.Schema) &&
                                !string.IsNullOrEmpty(element.Print))
                            {
                                System.Console.WriteLine(EvaluateValue(node, element.Print));
                            };
                        }
                        break;
                    case ElementAction.RegexReplace:
                        foreach (var node in selectedNodes.ToArray())
                        {
                            var originalXml = node.ToString(SaveOptions.DisableFormatting);
                            var replacedXml = Regex.Replace(originalXml, element.Regex, element.Replace, RegexOptions.Singleline);
                            var replacementNode = XElement.Parse(replacedXml, LoadOptions.PreserveWhitespace);

                            node.ReplaceWith(replacementNode);
                        }
                        break;
                    case ElementAction.Print:
                        foreach (var node in selectedNodes)
                        {
                            System.Console.WriteLine(EvaluateValue(node, element.Value));
                        }
                        break;
                    case ElementAction.OrderBySchema:
                        foreach (var node in selectedNodes)
                        {
                            var parentNode = node;
                            var childNodes = parentNode.Elements().ToArray();
                            var complexTypeName = element.ComplexType ?? node.Name.LocalName;
                            var schemaSet = BuildSchemaSet(node.Name.LocalName, complexTypeName, element.Schema);
                            var schema = schemaSet.Schemas().Cast<XmlSchema>().First(); //TODO: dubious, was .Single(), rewrite
                            var complexType = (XmlSchemaComplexType)schemaSet.GlobalTypes[new XmlQualifiedName(complexTypeName)];
                            var schemaNodeNames = ResolveSchemaElements(schema, complexType.ContentTypeParticle).Select(schemaNode => schemaNode.Name).ToArray();
                            var removeNodesDictionary = schemaNodeNames.ToDictionary(schemaNodeName => schemaNodeName, schemaNodeName => childNodes.Where(childNode => childNode.Name.LocalName == schemaNodeName).ToArray());
                            var nodeStack = new Stack<XNode>();

                            // remove child nodes in order as seen in schema
                            foreach (var schemaNodeName in schemaNodeNames)
                            {
                                var removeNodes = removeNodesDictionary[schemaNodeName];

                                if (removeNodes.Any())
                                {
                                    foreach (var removeNode in removeNodes)
                                    {
                                        removeNode.Remove();
                                        nodeStack.Push(removeNode);
                                    }
                                }
                            }

                            // insert child nodes back in correct order
                            while (nodeStack.Any())
                            {
                                var childNode = nodeStack.Pop();

                                parentNode.AddFirst(childNode);
                            }
                        }
                        break;
                    default:
                        throw new ArgumentException("Wrong action type", element.Action.ToString());
                }
            }
        }

        private static string PrintSqlInsert(XElement tableNode)
        {
            var columnNodes = tableNode.Elements().ToArray();
            var fields = columnNodes.Select(node => node.Name.LocalName);
            var values = columnNodes.Select(node => string.Format("{0}{1}{0}", bool.Parse(node.Attribute("quote").Value) ? "'" : string.Empty, node.Value));
            var insertSql = String.Format("  INSERT INTO {0} ({1}) VALUES ({2})", tableNode.Name.LocalName, fields.JoinBy(", "), values.JoinBy(", "));

            return insertSql;

        }

        private static string JoinBy(this IEnumerable<string> source, string separator)
        {
            return string.Join(separator, source.ToArray());
        }

        public static IEnumerable<TResult> ZipWith<TSource, TOther, TResult>(this IEnumerable<TSource> source, IEnumerable<TOther> other, Func<TSource, TOther, TResult> zipper)
        {
            var sourceEnumerator = source.GetEnumerator();
            var otherEnumerator = other.GetEnumerator();

            while (sourceEnumerator.MoveNext() &&
                   otherEnumerator.MoveNext())
            {
                yield return zipper(sourceEnumerator.Current, otherEnumerator.Current);
            }
        }

        private static readonly Regex SqlInsertRegex = new Regex(@"INSERT\s+INTO\s+(?<Table>\w+)\s*\((?:\s*(?<Columns>[^),\s]+)\s*?,?)*\s*\)\s*VALUES\s*\((?:\s*(?<Values>TO_DATE\([^)]*?\)|sysdate|'(?:[^']|'')*'|\d+)\s*,?)*\s*\)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        private static XElement ParseSqlInsert(string sqlInsert)
        {
            var match = SqlInsertRegex.Match(sqlInsert);
            Func<string, string> groupValue = name => match.Groups[name].Value.Trim();
            var tableName = groupValue("Table");
            var columns = match.Groups["Columns"].Captures.Cast<Capture>().Select(c => c.Value.Trim()).ToArray();
            var values = match.Groups["Values"].Captures.Cast<Capture>().Select(c => c.Value.Trim()).ToArray();
            var records = columns.ZipWith(values, (column, value) => new
                                                                         {
                                                                             Column = column,
                                                                             Value = value.Trim('\''),
                                                                             Quote = value.StartsWith("'") && value.EndsWith("'")
                                                                         });

            if (columns.Length != values.Length)
            {
                throw new InvalidOperationException("different number of columns and values"); //failure here means either malformed sql insert or bug in reqular expression
            }

            return new XElement(tableName, records.Select(record => new XElement(record.Column, record.Value, new XAttribute("quote", record.Quote))));
        }

        private static void AddElement(Element element, XContainer parentNode)
        {
            if (!element.Force) // if forcing is not enabled then test if element with such name exists
            {
                if (parentNode.XPathSelectElements(GetNewElementSearchXPath(parentNode, element)).Any())
                {
                    System.Console.WriteLine("Warning: not adding {0} because element with the same name already exists", element.Name);

                    return;
                }
            }

            var node = new XElement(element.Name);

            if (!string.IsNullOrEmpty(element.Value))
            {
                node.SetValue(EvaluateValue(parentNode, element.Value));
            }
            ProcessAttributes(node, element.Attributes);

            switch (element.Order)
            {
                case -1:
                    parentNode.Add(node);
                    break;
                case 0:
                    parentNode.AddFirst(node);
                    break;
                default:
                    parentNode.Elements().ElementAt(element.Order).AddBeforeSelf(node);
                    break;
            }
        }

        private static string GetNewElementSearchXPath(XNode node, Element element)
        {
            var matches = string.Empty;
            if (!string.IsNullOrEmpty(element.Value))
            {
                matches = string.Format("(.='{0}')", EvaluateValue(node, element.Value));
            }
            if (element.Attributes != null)
            {
                foreach (var attribute in element.Attributes)
                {
                    matches += ((matches.Length > 0) ? " and " : "") + string.Format("@{0}='{1}'", attribute.Name, EvaluateValue(node, attribute.Value));
                }
            }
            return (matches.Length > 0) ? string.Format("{0}[{1}]", element.Name, matches) : element.Name;
        }

        private static XElement RenameNode(XElement node, string namespaceURI, string newName)
        {
            var oldNode = node;
            var newNode = new XElement(XName.Get(newName, namespaceURI), oldNode.Nodes());

            newNode.ReplaceAttributes(oldNode.Attributes());

            oldNode.ReplaceWith(newNode);

            return newNode;
        }

        private static void ProcessAttributes(XElement node, IEnumerable<Attribute> attributes)
        {
            if (attributes == null) return;

            foreach (var attribute in attributes)
            {
                var value = EvaluateValue(node, attribute.Value);
                
                switch (attribute.Action)
                {
                    case AttributeAction.Add:
                        {
                            var newAttribute = new XAttribute(attribute.Name, value);

                            node.Add(newAttribute);
                        }
                        break;

                    case AttributeAction.Remove:
                        foreach (var a in node.Attributes(attribute.Name))
                        {
                            a.Remove();
                        }
                        break;

                    case AttributeAction.Update:
                        node.SetAttributeValue(attribute.Name, value);
                        break;

                    case AttributeAction.Rename:
                        var oldAttribute = node.Attribute(attribute.Name);
                        var oldAttributes = node.Attributes().ToList();

                        oldAttributes.Remove(oldAttribute);
                        oldAttributes.Add(new XAttribute(value, oldAttribute.Value));
                        
                        node.ReplaceAttributes(oldAttributes);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException("action");
                }
            }
        }

        private static readonly Regex EvaluationHandlerPattern = new Regex(@"^(?<handler>\w+)\[(?<expression>.*)\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string EvaluateValue(XNode node, string value)
        {
            if (string.IsNullOrEmpty(value)) return value;

            var match = EvaluationHandlerPattern.Match(value);

            if (!match.Success) return value;

            var handlerName = match.Groups["handler"].Value.ToLower();
            var expression = match.Groups["expression"].Value;

            switch (handlerName)
            {
                case "xpath":
                    return EvaluateXPath(node, expression);

                case "taxtoken":
                    return EvaluateTaxToken(node, expression);

                case "capitalize":
                    return EvaluateCapitalizeToken(node, expression);

                case "fixdate":
                    return EvaluateFixDateToken(node, expression);

                case "guid":
                    return EvaluateGuidToken(node, expression);

                case "uppercase":
                    return EvaluateUpperCaseToken(node, expression);
                    
                case "innerxml":
                    return EvaluateInnerXmlToken(node, expression);

                default:
                    throw new ArgumentOutOfRangeException("handlerName", handlerName, "Evaluation handler not found.");
            }
        }

        private static string EvaluateTaxToken(XNode node, string xpath)
        {
            var xPathVal = (string)node.CreateNavigator().Evaluate(xpath);
            var hashValue = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(xPathVal));
            var value = hashValue.Aggregate(new StringBuilder(), (current, t) => current.Append(t)).ToString();

            return value.Substring(0, 10);
        }

        private static string EvaluateXPath(XNode node, string xpath)
        {
            return (string) node.CreateNavigator().Evaluate(xpath);
        }

        private static string EvaluateCapitalizeToken(XNode node, string expression)
        {
            var value = EvaluateValue(node, expression);

            if (value.Length < 2) return value.ToUpper();

            return char.ToUpper(value[0]) + value.Substring(1);
        }

        private static string EvaluateFixDateToken(XNode node, string expression)
        {
            var value = EvaluateValue(node, expression);
            var date = DateTime.ParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static readonly Random Random = new Random(123456);

        private static string EvaluateGuidToken(XNode node, string expression)
        {
            if (expression == "repeatable")
            {
                var bytes = new byte[16];

                Random.NextBytes(bytes);

                return new Guid(bytes).ToString();
            }

            return Guid.NewGuid().ToString();
        }

        private static string EvaluateUpperCaseToken(XNode node, string expression)
        {
            var value = EvaluateValue(node, expression);

            return value.ToUpper();
        }

        private static string EvaluateInnerXmlToken(XNode node, string expression)
        {
            var targetNode = node.XPathSelectElement(expression);
            var innerXml = targetNode.CreateNavigator().InnerXml;

            return innerXml;
        }

        private static string PrettyPrint(string xml)
        {
            return XDocument.Parse(xml).ToString();
        }

        private static string PrettyPrint(XNode doc)
        {
            using (var writer = new StringWriter())
            {
                using (var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings { Indent = true }))
                {
                    doc.WriteTo(xmlWriter);
                }

                return writer.ToString();
            }
        }

        public static bool ValidateAgainstSchema(string xml, string elementName, string complexTypeName, string schemaFilename)
        {
            var result = true;

            //HACK: stripping quote attribute to allow simple type checking inside ParseSql context
            xml = Regex.Replace(xml, @"quote=""[^""]*""\s*", string.Empty);

            var settings = new XmlReaderSettings
            {
                ValidationType = ValidationType.Schema,
                Schemas = BuildSchemaSet(elementName, complexTypeName, schemaFilename)
            };

            settings.ValidationEventHandler += (sender, e) =>
            {
                var info = (IXmlLineInfo)sender;
                var line = info.HasLineInfo() ? info.LineNumber.ToString() : String.Empty;
                var position = info.HasLineInfo() ? info.LinePosition.ToString() : String.Empty;

                System.Console.WriteLine("Xml validation failed: {0} at ({1},{2})\r\nMessage: {3}", e.Severity, line, position, e.Message);
                System.Console.WriteLine("Xml:\r\n{0}", PrettyPrint(xml));

                result = false;
            };

            using (var input = new StringReader(xml))
            using (var reader = XmlReader.Create(input, settings))
            {
                while (reader.Read())
                {

                }
            }

            return result;
        }

        private static readonly Dictionary<string, XmlSchemaSet> BuildSchemaSetDictionary = new Dictionary<string, XmlSchemaSet>();

        /// <summary>
        /// pre-process schema by adding element with defined complex type
        /// </summary>
        private static XmlSchemaSet BuildSchemaSet(string elementName, string complexTypeName, string schemaFile)
        {
            if (string.IsNullOrEmpty(complexTypeName) || elementName == complexTypeName)       
            { 
                return BuildSchemaSetCached(schemaFile);
            }

            var cacheKey = string.Format("{0}|{1}|{2}", elementName, complexTypeName, schemaFile);
            XmlSchemaSet schemaSet;
            if (BuildSchemaSetDictionary.TryGetValue(cacheKey, out schemaSet))
            {
                return schemaSet;
            }

            var sourceSchema = CachedXmlSchemaRead(schemaFile);
            var auxiliarySchema = new XmlSchema();
            var include = new XmlSchemaInclude
                              {
                                  Schema = sourceSchema
                              };
            var element = new XmlSchemaElement
                              {
                                  Name = elementName,
                                  SchemaTypeName = new XmlQualifiedName(complexTypeName)
                              };

            auxiliarySchema.Includes.Add(include);
            auxiliarySchema.Items.Add(element);

            schemaSet = new XmlSchemaSet();

            schemaSet.Add(sourceSchema);
            schemaSet.Add(auxiliarySchema);

            schemaSet.Compile();

            return BuildSchemaSetDictionary[cacheKey] = schemaSet;
        }

        private static readonly Dictionary<string, XmlSchema> CachedXmlSchemaReadDictionary = new Dictionary<string, XmlSchema>();

        private static XmlSchema CachedXmlSchemaRead(string schemaFile)
        {
            XmlSchema schema;

            if (CachedXmlSchemaReadDictionary.TryGetValue(schemaFile, out schema))
            {
                return schema;
            }

            using (var reader = XmlReader.Create(schemaFile))
            {
                return CachedXmlSchemaReadDictionary[schemaFile] = XmlSchema.Read(reader, delegate { });
            }
        }

        private static readonly Dictionary<string, XmlSchemaSet> XmlSchemaSetCache = new Dictionary<string, XmlSchemaSet>();

        /// <summary>
        /// caching pre-processesed schema file by adding elements for every complex type
        /// </summary>
        private static XmlSchemaSet BuildSchemaSetCached(string schemaFile)
        {
            XmlSchemaSet schemas;

            if (XmlSchemaSetCache.TryGetValue(schemaFile, out schemas))
            {
                return schemas;
            }

            XmlSchema schema;

            using (var reader = XmlReader.Create(schemaFile))
            {
                schema = XmlSchema.Read(reader, delegate { });
            }

            var complexTypes = schema.Items.OfType<XmlSchemaComplexType>().ToArray();
            foreach (var complexType in complexTypes)
            {
                var element = new XmlSchemaElement
                                  {
                                      Name = complexType.Name,
                                      SchemaTypeName = new XmlQualifiedName(complexType.Name)
                                  };

                schema.Items.Add(element);
            }

            schemas = new XmlSchemaSet();

            schemas.Add(schema);

            schemas.Compile();

            return XmlSchemaSetCache[schemaFile] = schemas;
        }

        /// <summary>
        /// returns underlying schema elements, recursively flattening nested sequences and resolving groups
        /// </summary>
        /// <remark>
        /// assumes compiled schema
        /// </remark>
        public static IEnumerable<XmlSchemaElement> ResolveSchemaElements(XmlSchema schema, object o)
        {
            if (o == null)
            {
                yield break;
            }

            if (o is XmlSchemaElement)
            {
                yield return (XmlSchemaElement)o;
            }
            else
                if (o is XmlSchemaSequence)
                {
                    foreach (var item in ((XmlSchemaSequence)o).Items)
                        foreach (var element in ResolveSchemaElements(schema, item))
                            yield return element;
                }
                else
                    if (o is XmlSchemaGroupRef)
                    {
                        var groupRef = o as XmlSchemaGroupRef;
                        var group = schema.Groups[groupRef.RefName] as XmlSchemaGroup;

                        foreach (var element in ResolveSchemaElements(schema, group.Particle))
                            yield return element;
                    }
                    else
                    {
                        throw new ArgumentException(o.GetType().Name + " not supported!");
                    }
        }

        private static IEnumerable<XElement> OrderBy(this IEnumerable<XElement> source, ProcessingOrder order)
        {
            switch (order)
            {
                case ProcessingOrder.Document:
                    return source;

                case ProcessingOrder.TopDown:
                    return
                        from element in source
                        let depth = element.AncestorsAndSelf().Count()
                        orderby depth ascending
                        select element;

                case ProcessingOrder.BottomUp:
                    return
                        from element in source
                        let depth = element.AncestorsAndSelf().Count()
                        orderby depth descending
                        select element;

                default:
                    throw new ArgumentOutOfRangeException("order");
            }
        }
    }
}
