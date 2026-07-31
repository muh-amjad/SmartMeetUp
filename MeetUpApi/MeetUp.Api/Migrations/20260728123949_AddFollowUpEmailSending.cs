using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeetUp.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowUpEmailSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OptOutFollowUpEmails",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "FollowUpEmailRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FollowUpEmailId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientUserId = table.Column<string>(type: "text", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUpEmailRecipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FollowUpEmailRecipients_AspNetUsers_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_FollowUpEmailRecipients_FollowUpEmails_FollowUpEmailId",
                        column: x => x.FollowUpEmailId,
                        principalTable: "FollowUpEmails",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpEmailRecipients_FollowUpEmailId",
                table: "FollowUpEmailRecipients",
                column: "FollowUpEmailId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUpEmailRecipients_RecipientUserId",
                table: "FollowUpEmailRecipients",
                column: "RecipientUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FollowUpEmailRecipients");

            migrationBuilder.DropColumn(
                name: "OptOutFollowUpEmails",
                table: "AspNetUsers");
        }
    }
}
