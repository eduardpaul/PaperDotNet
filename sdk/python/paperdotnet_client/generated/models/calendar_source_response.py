from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class CalendarSourceResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The created property
    created: Optional[int] = None
    # The error property
    error: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The lastSuccess property
    last_success: Optional[datetime.datetime] = None
    # The name property
    name: Optional[str] = None
    # The OdataEtag property
    odata_etag: Optional[str] = None
    # The paused property
    paused: Optional[bool] = None
    # The refreshing property
    refreshing: Optional[bool] = None
    # The removed property
    removed: Optional[int] = None
    # The updated property
    updated: Optional[int] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CalendarSourceResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CalendarSourceResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CalendarSourceResponse()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "created": lambda n : setattr(self, 'created', n.get_int_value()),
            "error": lambda n : setattr(self, 'error', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "lastSuccess": lambda n : setattr(self, 'last_success', n.get_datetime_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
            "paused": lambda n : setattr(self, 'paused', n.get_bool_value()),
            "refreshing": lambda n : setattr(self, 'refreshing', n.get_bool_value()),
            "removed": lambda n : setattr(self, 'removed', n.get_int_value()),
            "updated": lambda n : setattr(self, 'updated', n.get_int_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_int_value("created", self.created)
        writer.write_str_value("error", self.error)
        writer.write_uuid_value("id", self.id)
        writer.write_datetime_value("lastSuccess", self.last_success)
        writer.write_str_value("name", self.name)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_bool_value("paused", self.paused)
        writer.write_bool_value("refreshing", self.refreshing)
        writer.write_int_value("removed", self.removed)
        writer.write_int_value("updated", self.updated)
        writer.write_additional_data_value(self.additional_data)
