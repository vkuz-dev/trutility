using System.Collections.Generic;
using System.Linq;

using Xunit;

namespace trutility.Tests
{
    public class MenuSearchTests
    {
        private static SearchEntry Entry(string name, string location = "", bool yours = false) =>
            new SearchEntry { Name = name, Location = location, IsYours = yours };

        private static List<string> Find(string query, params SearchEntry[] index) =>
            MenuSearch.Find(index, query).Select(e => e.Name).ToList();

        [Fact]
        public void EveryWordMustMatchNameOrLocationInAnyOrder()
        {
            List<string> hits = Find("pay fin",
                Entry("payroll.xlsx", "Finance ▸ Reports"),
                Entry("payroll.xlsx", "HR"),
                Entry("budget.xlsx", "Finance"));

            Assert.Equal(new[] { "payroll.xlsx" }, hits);
        }

        [Fact]
        public void NameStartingWithQueryRanksFirst()
        {
            List<string> hits = Find("pay",
                Entry("old-payroll"),
                Entry("report", "payments"),
                Entry("payroll"));

            Assert.Equal(new[] { "payroll", "old-payroll", "report" }, hits);
        }

        [Fact]
        public void SharedResultsStayAboveYours()
        {
            List<SearchEntry> hits = MenuSearch.Find(new[]
            {
                Entry("putty", yours: true),
                Entry("old putty"),
            }, "putty");

            Assert.Equal(new[] { false, true }, hits.Select(h => h.IsYours));
        }

        [Fact]
        public void BlankQueryFindsNothing()
        {
            Assert.Empty(Find("   ", Entry("a")));
        }

        [Fact]
        public void ShortLocationKeepsTwoLevels()
        {
            Assert.Equal("A ▸ B ▸ …", MenuSearch.ShortLocation("A ▸ B ▸ C ▸ D", 2));
            Assert.Equal("A ▸ B", MenuSearch.ShortLocation("A ▸ B", 2));
        }
    }
}
