using Irony.Parsing;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Protocol;
using System;
using System.Collections.Generic;

namespace Microsoft.WindowsAzure.Storage.ChainTableInterface
{
    // All the parsing stuff is thrown together, but it appears to work...

    class FilterStringGrammar : Grammar
    {
        internal readonly NonTerminal comparison, booleanOpExpr;
        FilterStringGrammar()
        {
            var propertyName = new IdentifierTerminal("propertyName");
            var compareOp = new NonTerminal("compareOp");
            compareOp.Rule = ToTerm(QueryComparisons.Equal) | ToTerm(QueryComparisons.NotEqual)
                | ToTerm(QueryComparisons.LessThan) | ToTerm(QueryComparisons.LessThanOrEqual)
                | ToTerm(QueryComparisons.GreaterThan) | ToTerm(QueryComparisons.GreaterThanOrEqual);
            var stringLiteral = new StringLiteral("stringLiteral", "'", StringOptions.AllowsDoubledQuote);
            var booleanLiteral = new ConstantTerminal("booleanLiteral");
            booleanLiteral.Add("true", true);
            booleanLiteral.Add("false", false);
            var literal = new NonTerminal("literal");
            literal.Rule = stringLiteral | booleanLiteral;
            comparison = new NonTerminal("comparison");
            comparison.Rule = propertyName + compareOp + literal;
            var booleanOp = new NonTerminal("booleanOp");
            booleanOp.Rule = ToTerm(TableOperators.And) | ToTerm(TableOperators.Or);
            var booleanExpr = new NonTerminal("booleanExpr");
            booleanOpExpr = new NonTerminal("booleanOpExpr");
            booleanOpExpr.Rule = "(" + booleanExpr + ")" + booleanOp + "(" + booleanExpr + ")";
            booleanExpr.Rule = comparison | booleanOpExpr;
            Root = booleanExpr;
        }

        internal static readonly FilterStringGrammar Grammar = new FilterStringGrammar();
        internal static readonly Parser Parser = new Parser(Grammar);
    }

    public abstract class FilterExpression
    {
        // ITableEntity does not provide random access to properties. :(
        public abstract bool Evaluate(string partitionKey, string rowKey, IDictionary<string, EntityProperty> properties);
        public bool Evaluate(ITableEntity entity)
        {
            return Evaluate(entity.PartitionKey, entity.RowKey, entity.WriteEntity(null));
        }
        public abstract string ToFilterString();
    }
    public class EmptyFilterExpression : FilterExpression
    {
        public override bool Evaluate(string partitionKey, string rowKey, IDictionary<string, EntityProperty> properties)
        {
            return true;
        }

        public override string ToFilterString()
        {
            return "";
        }
    }
    public class ComparisonExpression : FilterExpression
    {
        public string PropertyName { get; set;  }
        public string Operator { get; set;  }
        public IComparable Value { get; set;  }
        internal ComparisonExpression(string propertyName, string theOperator, IComparable value)
        {
            PropertyName = propertyName;
            Operator = theOperator;
            Value = value;
        }

        public override bool Evaluate(string partitionKey, string rowKey, IDictionary<string, EntityProperty> properties)
        {
            IComparable valueFromEntity;
            EntityProperty entityProperty;
            if (PropertyName == TableConstants.PartitionKey)
                valueFromEntity = partitionKey;
            else if (PropertyName == TableConstants.RowKey)
                valueFromEntity = rowKey;
            else if (properties.TryGetValue(PropertyName, out entityProperty))
                // I think this should work.  Not certain the comparison semantics
                // are the same as real Azure for the more complex property types.
                valueFromEntity = (IComparable)entityProperty.PropertyAsObject;
            else
                return false;
            try
            {
                switch (Operator)
                {
                    case QueryComparisons.Equal:
                        return Equals(valueFromEntity, Value);
                    case QueryComparisons.NotEqual:
                        return !Equals(valueFromEntity, Value);
                    case QueryComparisons.LessThan:
                        return valueFromEntity.CompareTo(Value) < 0;
                    case QueryComparisons.LessThanOrEqual:
                        return valueFromEntity.CompareTo(Value) <= 0;
                    case QueryComparisons.GreaterThan:
                        return valueFromEntity.CompareTo(Value) > 0;
                    case QueryComparisons.GreaterThanOrEqual:
                        return valueFromEntity.CompareTo(Value) >= 0;
                    default:
                        throw new NotImplementedException();  // Should not be reached.
                }
            }
            catch (ArgumentException)
            {
                // Operands have different types.
                // I was unable to figure out the behavior of real Azure in this case with a few tests.
                return false;
            }
        }

        public override string ToFilterString()
        {
            string stringValue;
            bool? boolValue;
            if ((stringValue = Value as string) != null)
                return TableQuery.GenerateFilterCondition(PropertyName, Operator, stringValue);
            else if ((boolValue = Value as bool?) != null)
                return TableQuery.GenerateFilterConditionForBool(PropertyName, Operator, boolValue.Value);
            else
                throw new NotImplementedException();  // Should not be reached.
        }
    }
    public class BooleanOperatorExpression : FilterExpression
    {
        public FilterExpression Left { get; set; }
        public string Operator { get; set;  }
        public FilterExpression Right { get; set;  }
        internal BooleanOperatorExpression(FilterExpression left, string theOperator, FilterExpression right)
        {
            Left = left;
            Operator = theOperator;
            Right = right;
        }

        public override bool Evaluate(string partitionKey, string rowKey, IDictionary<string, EntityProperty> properties)
        {
            switch (Operator)
            {
                case TableOperators.And:
                    return Left.Evaluate(partitionKey, rowKey, properties) && Right.Evaluate(partitionKey, rowKey, properties);
                case TableOperators.Or:
                    return Left.Evaluate(partitionKey, rowKey, properties) || Right.Evaluate(partitionKey, rowKey, properties);
                default:
                    throw new NotImplementedException();  // Should not be reached.
            }
        }

        public override string ToFilterString()
        {
            return TableQuery.CombineFilters(Left.ToFilterString(), Operator, Right.ToFilterString());
        }
    }

    public static partial class ChainTableUtils
    {
        private static readonly object lockObject = new object();

        private static FilterExpression MakeFilterExpression(ParseTreeNode node)
        {
            node = node.ChildNodes[0];
            if (node.Term == FilterStringGrammar.Grammar.comparison)
            {
                return new ComparisonExpression(
                    node.ChildNodes[0].FindTokenAndGetText(),
                    node.ChildNodes[1].FindTokenAndGetText(),
                    (IComparable)node.ChildNodes[2].FindToken().Value);
            }
            else if (node.Term == FilterStringGrammar.Grammar.booleanOpExpr)
            {
                return new BooleanOperatorExpression(
                    MakeFilterExpression(node.ChildNodes[1]),
                    node.ChildNodes[3].FindTokenAndGetText(),
                    MakeFilterExpression(node.ChildNodes[5]));
            }
            else
                throw new NotImplementedException();  // Should not be reached
        }

        // This is intended to support filter strings generated with
        // TableQuery.CombineFilters and TableQuery.GenerateFilterCondition
        // (currently string and bool only), plus the empty string.
        public static FilterExpression ParseFilterString(string filterString)
        {
            if (string.IsNullOrEmpty(filterString))
                return new EmptyFilterExpression();
            {
                lock (lockObject)
                {
                    ParseTree parseTree = FilterStringGrammar.Parser.Parse(filterString);
                    if (parseTree.Status != ParseTreeStatus.Parsed)
                        throw new ArgumentException("Failed to parse filter string");
                    return MakeFilterExpression(parseTree.Root);
                }
            }
        }
    }
}
