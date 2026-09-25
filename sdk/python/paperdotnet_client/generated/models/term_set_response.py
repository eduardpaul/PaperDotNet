from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class TermSetResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The description property
    description: Optional[str] = None
    # The groupId property
    group_id: Optional[UUID] = None
    # The id property
    id: Optional[UUID] = None
    # The isKeywords property
    is_keywords: Optional[bool] = None
    # The isOpen property
    is_open: Optional[bool] = None
    # The name property
    name: Optional[str] = None
    # The ETag for `If-Match` on changes (the same as the `ETag` header).
    odata_etag: Optional[str] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> TermSetResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: TermSetResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return TermSetResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "groupId": lambda n : setattr(self, 'group_id', n.get_uuid_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "isKeywords": lambda n : setattr(self, 'is_keywords', n.get_bool_value()),
            "isOpen": lambda n : setattr(self, 'is_open', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
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
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("description", self.description)
        writer.write_uuid_value("groupId", self.group_id)
        writer.write_uuid_value("id", self.id)
        writer.write_bool_value("isKeywords", self.is_keywords)
        writer.write_bool_value("isOpen", self.is_open)
        writer.write_str_value("name", self.name)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_additional_data_value(self.additional_data)
    

